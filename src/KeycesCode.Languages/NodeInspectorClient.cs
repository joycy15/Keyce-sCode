using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KeycesCode.Languages;

internal sealed class NodeInspectorClient : IDebugClient
{
    private readonly Process _process;
    private readonly ClientWebSocket _socket;
    private readonly Action<ProcessOutput> _report;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<int, JsonElement> _frames = [];
    private readonly Dictionary<int, string> _variableReferences = [];
    private readonly Dictionary<string, List<string>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingBreakpoint> _pendingBreakpoints = new(StringComparer.Ordinal);
    private readonly Task _receiver;
    private readonly Task _outputReader;
    private readonly Task _errorReader;
    private JsonElement? _callFrames;
    private int _cdpSequence;
    private int _dapSequence;
    private int _referenceSequence;
    private bool _isConfigured;
    private bool _isPaused;
    private int _terminationSent;
    private int _disposed;

    private NodeInspectorClient(Process process, ClientWebSocket socket, Action<ProcessOutput> report)
    {
        _process = process;
        _socket = socket;
        _report = report;
        _receiver = ReceiveMessagesAsync(_shutdown.Token);
        _outputReader = ReadProcessOutputAsync(_process.StandardOutput, false, _shutdown.Token);
        _errorReader = ReadProcessOutputAsync(_process.StandardError, true, _shutdown.Token);
    }

    public event Action<JsonElement>? EventReceived;

    public static async Task<NodeInspectorClient> StartAsync(
        string nodePath,
        string programPath,
        IReadOnlyList<string> runtimeArguments,
        string workingDirectory,
        Action<ProcessOutput> report,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(nodePath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--inspect-brk=127.0.0.1:0");
        foreach (var argument in runtimeArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(programPath);
        Process? process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Impossible de démarrer Node.js sous le débogueur.");
        }

        ClientWebSocket? socket = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            string? webSocketUrl = null;
            while (webSocketUrl is null)
            {
                var line = await process.StandardError.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    throw new InvalidOperationException("Node.js a quitté avant d’ouvrir son port de débogage.");
                }

                var match = Regex.Match(line, @"ws://[^\s]+", RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    webSocketUrl = match.Value;
                }
                else
                {
                    report(new ProcessOutput(line));
                }
            }

            socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(webSocketUrl), timeout.Token);
            var client = new NodeInspectorClient(process, socket, report);
            await client.SendInspectorRequestAsync("Runtime.enable", new { }, cancellationToken);
            await client.SendInspectorRequestAsync("Debugger.enable", new { }, cancellationToken);
            process = null;
            socket = null;
            return client;
        }
        finally
        {
            socket?.Dispose();
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                process.Dispose();
            }
        }
    }

    public async Task<JsonElement> RequestAsync(
        string command,
        object? arguments,
        CancellationToken cancellationToken)
    {
        var requestSequence = Interlocked.Increment(ref _dapSequence);
        var args = JsonSerializer.SerializeToElement(arguments ?? new { });
        switch (command)
        {
            case "initialize":
                return DapResponse(command, requestSequence);
            case "launch":
                EmitEvent("initialized", new { });
                return DapResponse(command, requestSequence);
            case "setBreakpoints":
                return await SetBreakpointsAsync(command, requestSequence, args, cancellationToken);
            case "configurationDone":
                _isConfigured = true;
                if (_isPaused)
                {
                    EmitStoppedEvent();
                }
                else
                {
                    await SendInspectorRequestAsync("Runtime.runIfWaitingForDebugger", new { }, cancellationToken);
                }

                return DapResponse(command, requestSequence);
            case "threads":
                return DapResponse(command, requestSequence, new { threads = new[] { new { id = 1, name = "Node.js" } } });
            case "stackTrace":
                return DapResponse(command, requestSequence, GetStackTrace());
            case "scopes":
                return DapResponse(command, requestSequence, GetScopes(args));
            case "variables":
                return await GetVariablesAsync(command, requestSequence, args, cancellationToken);
            case "continue":
                await SendInspectorRequestAsync("Debugger.resume", new { }, cancellationToken);
                return DapResponse(command, requestSequence, new { allThreadsContinued = true });
            case "next":
                await SendInspectorRequestAsync("Debugger.stepOver", new { }, cancellationToken);
                return DapResponse(command, requestSequence);
            case "stepIn":
                await SendInspectorRequestAsync("Debugger.stepInto", new { }, cancellationToken);
                return DapResponse(command, requestSequence);
            case "stepOut":
                await SendInspectorRequestAsync("Debugger.stepOut", new { }, cancellationToken);
                return DapResponse(command, requestSequence);
            case "pause":
                await SendInspectorRequestAsync("Debugger.pause", new { }, cancellationToken);
                return DapResponse(command, requestSequence);
            case "disconnect":
                await StopAsync(cancellationToken);
                return DapResponse(command, requestSequence);
            default:
                throw new NotSupportedException($"La commande DAP « {command} » n’est pas prise en charge par Node.js.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        _socket.Abort();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(cancellationToken);
            }
        }
        catch (InvalidOperationException exception)
        {
            _report(new ProcessOutput($"Arrêt de Node.js : {exception.Message}", true));
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            _report(new ProcessOutput($"Arrêt de Node.js : {exception.Message}", true));
        }
        finally
        {
            Cleanup();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        _socket.Abort();
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException exception)
            {
                _report(new ProcessOutput($"Arrêt de Node.js : {exception.Message}", true));
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                _report(new ProcessOutput($"Arrêt de Node.js : {exception.Message}", true));
            }
        }

        Cleanup();
    }

    private void Cleanup()
    {
        _socket.Dispose();
        _process.Dispose();
        _sendLock.Dispose();
        _shutdown.Dispose();
    }

    private async Task<JsonElement> SetBreakpointsAsync(
        string command,
        int requestSequence,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var source = arguments.GetProperty("source");
        var path = Path.GetFullPath(source.GetProperty("path").GetString()!);
        if (_breakpoints.TryGetValue(path, out var previousIds))
        {
            foreach (var breakpointId in previousIds)
            {
                await SendInspectorRequestAsync("Debugger.removeBreakpoint", new { breakpointId }, cancellationToken);
                _pendingBreakpoints.Remove(breakpointId);
            }
        }

        var ids = new List<string>();
        var breakpoints = new List<object>();
        foreach (var breakpoint in arguments.GetProperty("breakpoints").EnumerateArray())
        {
            var requestedLine = breakpoint.GetProperty("line").GetInt32();
            var response = await SendInspectorRequestAsync("Debugger.setBreakpointByUrl", new
            {
                url = new Uri(path).AbsoluteUri,
                lineNumber = requestedLine - 1,
                columnNumber = 0
            }, cancellationToken);
            var body = response.GetProperty("result");
            var breakpointId = body.GetProperty("breakpointId").GetString()!;
            ids.Add(breakpointId);
            var locations = body.GetProperty("locations");
            var verified = locations.GetArrayLength() > 0;
            var breakpointLine = verified
                ? locations[0].GetProperty("lineNumber").GetInt32() + 1
                : requestedLine;
            var dapBreakpointId = ++_referenceSequence;
            _pendingBreakpoints[breakpointId] = new PendingBreakpoint(dapBreakpointId, path, requestedLine);
            breakpoints.Add(new
            {
                id = dapBreakpointId,
                verified,
                line = breakpointLine,
                message = verified ? null : "En attente du chargement du fichier."
            });
        }

        _breakpoints[path] = ids;
        return DapResponse(command, requestSequence, new { breakpoints });
    }

    private object GetStackTrace()
    {
        _frames.Clear();
        var frames = new List<object>();
        if (_callFrames is not { } callFrames)
        {
            return new { stackFrames = frames, totalFrames = 0 };
        }

        var index = 0;
        foreach (var frame in callFrames.EnumerateArray())
        {
            var id = ++index;
            _frames[id] = frame.Clone();
            var location = frame.GetProperty("location");
            var sourceUrl = frame.TryGetProperty("url", out var url) ? url.GetString() : null;
            var sourcePath = GetLocalPath(sourceUrl);
            frames.Add(new
            {
                id,
                name = frame.TryGetProperty("functionName", out var functionName) &&
                       !string.IsNullOrEmpty(functionName.GetString())
                    ? functionName.GetString()
                    : "(fonction anonyme)",
                line = location.GetProperty("lineNumber").GetInt32() + 1,
                column = location.GetProperty("columnNumber").GetInt32() + 1,
                source = sourcePath is null
                    ? null
                    : new { name = Path.GetFileName(sourcePath), path = sourcePath }
            });
        }

        return new { stackFrames = frames, totalFrames = frames.Count };
    }

    private object GetScopes(JsonElement arguments)
    {
        var frameId = arguments.GetProperty("frameId").GetInt32();
        if (!_frames.TryGetValue(frameId, out var frame) ||
            !frame.TryGetProperty("scopeChain", out var scopeChain))
        {
            return new { scopes = Array.Empty<object>() };
        }

        var scopes = new List<object>();
        foreach (var scope in scopeChain.EnumerateArray())
        {
            if (!scope.TryGetProperty("object", out var scopeObject) ||
                !scopeObject.TryGetProperty("objectId", out var objectId))
            {
                continue;
            }

            var reference = ++_referenceSequence;
            _variableReferences[reference] = objectId.GetString()!;
            var scopeName = scope.TryGetProperty("type", out var type) ? type.GetString() : "local";
            scopes.Add(new
            {
                name = scopeName,
                variablesReference = reference,
                expensive = scopeName == "global"
            });
        }

        return new { scopes };
    }

    private async Task<JsonElement> GetVariablesAsync(
        string command,
        int requestSequence,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var reference = arguments.GetProperty("variablesReference").GetInt32();
        if (!_variableReferences.TryGetValue(reference, out var objectId))
        {
            return DapResponse(command, requestSequence, new { variables = Array.Empty<object>() });
        }

        var response = await SendInspectorRequestAsync("Runtime.getProperties", new
        {
            objectId,
            ownProperties = true,
            accessorPropertiesOnly = false,
            generatePreview = false
        }, cancellationToken);
        var descriptors = response.GetProperty("result").GetProperty("result");
        var variables = new List<object>();
        foreach (var descriptor in descriptors.EnumerateArray())
        {
            if (!descriptor.TryGetProperty("value", out var value))
            {
                continue;
            }

            var nestedReference = 0;
            if (value.TryGetProperty("objectId", out var nestedObjectId))
            {
                nestedReference = ++_referenceSequence;
                _variableReferences[nestedReference] = nestedObjectId.GetString()!;
            }

            variables.Add(new
            {
                name = descriptor.TryGetProperty("name", out var name) ? name.GetString() : "?",
                value = FormatRemoteValue(value),
                type = value.TryGetProperty("type", out var type) ? type.GetString() : null,
                variablesReference = nestedReference
            });
        }

        return DapResponse(command, requestSequence, new { variables });
    }

    private async Task<JsonElement> SendInspectorRequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _cdpSequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Impossible d’enregistrer la requête d’inspection Node.js.");
        }

        try
        {
            var message = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await _socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Node.js n’a pas répondu à la commande « {method} ».");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[16 * 1024];
            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        EmitTerminatedEvent();
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                using var document = JsonDocument.Parse(message.ToArray());
                DispatchInspectorMessage(document.RootElement);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _report(new ProcessOutput($"Connexion au débogueur Node.js interrompue : {exception.Message}", true));
                EmitTerminatedEvent();
            }
        }
        catch (JsonException exception)
        {
            _report(new ProcessOutput($"Message d’inspection Node.js invalide : {exception.Message}", true));
            EmitTerminatedEvent();
        }
    }

    private void DispatchInspectorMessage(JsonElement message)
    {
        if (message.TryGetProperty("id", out var idElement))
        {
            var id = idElement.GetInt32();
            if (_pending.TryGetValue(id, out var completion))
            {
                if (message.TryGetProperty("error", out var error))
                {
                    completion.TrySetException(new InvalidOperationException(
                        error.TryGetProperty("message", out var errorMessage)
                            ? errorMessage.GetString()
                            : "La commande d’inspection Node.js a échoué."));
                }
                else
                {
                    completion.TrySetResult(message.Clone());
                }
            }

            return;
        }

        if (!message.TryGetProperty("method", out var methodElement) ||
            !message.TryGetProperty("params", out var parameters))
        {
            return;
        }

        switch (methodElement.GetString())
        {
            case "Debugger.paused":
                _isPaused = true;
                _callFrames = parameters.GetProperty("callFrames").Clone();
                _report(new ProcessOutput("Le débogueur Node.js a suspendu l’exécution."));
                if (_isConfigured)
                {
                    EmitStoppedEvent(parameters.TryGetProperty("reason", out var reason) ? reason.GetString() : null);
                }

                break;
            case "Debugger.resumed":
                _isPaused = false;
                _report(new ProcessOutput("Le débogueur Node.js a repris l’exécution."));
                if (_isConfigured)
                {
                    EmitEvent("continued", new { threadId = 1, allThreadsContinued = true });
                }

                break;
            case "Debugger.breakpointResolved":
                var breakpointId = parameters.GetProperty("breakpointId").GetString();
                if (breakpointId is not null &&
                    _pendingBreakpoints.TryGetValue(breakpointId, out var pendingBreakpoint))
                {
                    var location = parameters.GetProperty("location");
                    EmitEvent("breakpoint", new
                    {
                        reason = "changed",
                        breakpoint = new
                        {
                            id = pendingBreakpoint.Id,
                            verified = true,
                            line = location.GetProperty("lineNumber").GetInt32() + 1,
                            source = new { name = Path.GetFileName(pendingBreakpoint.SourcePath), path = pendingBreakpoint.SourcePath }
                        }
                    });
                }

                break;
            case "Runtime.consoleAPICalled":
                var output = string.Join(" ", parameters.GetProperty("args").EnumerateArray().Select(FormatRemoteValue));
                _report(new ProcessOutput(output));
                break;
            case "Runtime.exceptionThrown":
                var exceptionText = parameters.GetProperty("exceptionDetails").TryGetProperty("text", out var text)
                    ? text.GetString()
                    : "Exception JavaScript";
                _report(new ProcessOutput(exceptionText ?? "Exception JavaScript", true));
                break;
        }
    }

    private static string FormatRemoteValue(JsonElement remoteValue)
    {
        if (remoteValue.TryGetProperty("value", out var value))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        }

        return remoteValue.TryGetProperty("description", out var description)
            ? description.GetString() ?? string.Empty
            : remoteValue.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty;
    }

    private static string? GetLocalPath(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            return null;
        }

        return uri.LocalPath;
    }

    private void EmitStoppedEvent(string? reason = null) =>
        EmitEvent("stopped", new
        {
            reason = reason?.Contains("start", StringComparison.OrdinalIgnoreCase) == true ? "entry" : "breakpoint",
            threadId = 1,
            allThreadsStopped = true
        });

    private void EmitTerminatedEvent()
    {
        if (Interlocked.Exchange(ref _terminationSent, 1) == 0)
        {
            EmitEvent("terminated", new { });
        }
    }

    private void EmitEvent(string eventName, object body)
    {
        var message = JsonSerializer.SerializeToElement(new
        {
            seq = Interlocked.Increment(ref _dapSequence),
            type = "event",
            @event = eventName,
            body
        });
        EventReceived?.Invoke(message);
    }

    private JsonElement DapResponse(string command, int requestSequence, object? body = null) =>
        JsonSerializer.SerializeToElement(new
        {
            seq = Interlocked.Increment(ref _dapSequence),
            type = "response",
            request_seq = requestSequence,
            success = true,
            command,
            body
        });

    private async Task ReadProcessOutputAsync(
        StreamReader reader,
        bool isError,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                _report(new ProcessOutput(line, isError));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _report(new ProcessOutput($"Sortie Node.js interrompue : {exception.Message}", true));
            }
        }
    }

    private sealed record PendingBreakpoint(int Id, string SourcePath, int RequestedLine);
}
