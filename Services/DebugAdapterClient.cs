using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Keyce_sCode.Services;

internal interface IDebugClient : IDisposable
{
    event Action<JsonElement>? EventReceived;
    Task<JsonElement> RequestAsync(string command, object? arguments, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed class DebugAdapterClient : IDebugClient
{
    private readonly Process _process;
    private readonly Action<ProcessOutput> _report;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readerTask;
    private readonly Task _errorReaderTask;
    private int _sequence;
    private int _disposed;

    private DebugAdapterClient(Process process, Action<ProcessOutput> report)
    {
        _process = process;
        _report = report;
        _readerTask = ReadMessagesAsync(_shutdown.Token);
        _errorReaderTask = ReadErrorsAsync(_shutdown.Token);
    }

    public event Action<JsonElement>? EventReceived;

    public static DebugAdapterClient Start(ProcessStartInfo startInfo, Action<ProcessOutput> report)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        startInfo.StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        startInfo.StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Impossible de démarrer l’adaptateur « {startInfo.FileName} ».");
        }

        return new DebugAdapterClient(process, report);
    }

    public async Task<JsonElement> RequestAsync(
        string command,
        object? arguments,
        CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(sequence, completion))
        {
            throw new InvalidOperationException("Impossible d’enregistrer la requête de débogage.");
        }

        var message = new Dictionary<string, object?>
        {
            ["seq"] = sequence,
            ["type"] = "request",
            ["command"] = command
        };
        if (arguments is not null)
        {
            message["arguments"] = arguments;
        }

        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(message);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _process.StandardInput.BaseStream.WriteAsync(header, cancellationToken);
                await _process.StandardInput.BaseStream.WriteAsync(body, cancellationToken);
                await _process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"L’adaptateur n’a pas répondu à la requête « {command} » en 30 secondes.");
        }
        finally
        {
            _pendingRequests.TryRemove(sequence, out _);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_process.HasExited)
            {
                await RequestAsync("disconnect", new { restart = false, terminateDebuggee = true }, cancellationToken);
            }
        }
        catch (IOException exception)
        {
            _report(new ProcessOutput($"Déconnexion du débogueur : {exception.Message}", true));
        }
        catch (InvalidOperationException exception)
        {
            _report(new ProcessOutput($"Déconnexion du débogueur : {exception.Message}", true));
        }
        catch (TimeoutException exception)
        {
            _report(new ProcessOutput($"Déconnexion du débogueur : {exception.Message}", true));
        }
        catch (OperationCanceledException exception)
        {
            _report(new ProcessOutput($"Déconnexion du débogueur annulée : {exception.Message}", true));
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException exception)
            {
                _report(new ProcessOutput($"Arrêt de l’adaptateur : {exception.Message}", true));
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                _report(new ProcessOutput($"Arrêt de l’adaptateur : {exception.Message}", true));
            }
        }

        _process.Dispose();
        _writeLock.Dispose();
        _shutdown.Dispose();
    }

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stream = _process.StandardOutput.BaseStream;
            while (!cancellationToken.IsCancellationRequested)
            {
                var contentLength = await ReadContentLengthAsync(stream, cancellationToken);
                if (contentLength is null)
                {
                    break;
                }

                var body = new byte[contentLength.Value];
                await ReadExactlyAsync(stream, body, cancellationToken);
                using var document = JsonDocument.Parse(body);
                DispatchMessage(document.RootElement);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            FailPendingRequests(exception);
            _report(new ProcessOutput($"Connexion à l’adaptateur interrompue : {exception.Message}", true));
        }
        catch (JsonException exception)
        {
            FailPendingRequests(exception);
            _report(new ProcessOutput($"Message DAP invalide : {exception.Message}", true));
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            FailPendingRequests(new EndOfStreamException("L’adaptateur de débogage a fermé sa sortie."));
        }
    }

    private async Task ReadErrorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   await _process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                _report(new ProcessOutput($"[débogueur] {line}", true));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _report(new ProcessOutput($"Sortie du débogueur interrompue : {exception.Message}", true));
            }
        }
    }

    private void DispatchMessage(JsonElement message)
    {
        if (!message.TryGetProperty("type", out var type))
        {
            throw new JsonException("Le message DAP ne contient pas de type.");
        }

        if (type.GetString() == "response")
        {
            var requestSequence = message.GetProperty("request_seq").GetInt32();
            if (!_pendingRequests.TryGetValue(requestSequence, out var completion))
            {
                return;
            }

            if (message.GetProperty("success").GetBoolean())
            {
                completion.TrySetResult(message.Clone());
            }
            else
            {
                var error = message.TryGetProperty("message", out var errorMessage)
                    ? errorMessage.GetString()
                    : "L’adaptateur a refusé la requête.";
                completion.TrySetException(new InvalidOperationException(error));
            }

            return;
        }

        if (type.GetString() == "event")
        {
            EventReceived?.Invoke(message.Clone());
        }
    }

    private static async Task<int?> ReadContentLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var header = new MemoryStream();
        var buffer = new byte[1];
        var terminatorMatched = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return header.Length == 0 ? null : throw new EndOfStreamException("En-tête DAP incomplet.");
            }

            header.WriteByte(buffer[0]);
            terminatorMatched = (terminatorMatched, buffer[0]) switch
            {
                (0, 13) => 1,
                (1, 10) => 2,
                (2, 13) => 3,
                (3, 10) => 4,
                (_, 13) => 1,
                _ => 0
            };
            if (terminatorMatched == 4)
            {
                break;
            }

            if (header.Length > 8192)
            {
                throw new InvalidDataException("L’en-tête DAP est trop volumineux.");
            }
        }

        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var contentLengthLine = lines.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (contentLengthLine is null ||
            !int.TryParse(contentLengthLine.AsSpan("Content-Length:".Length).Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var contentLength) ||
            contentLength < 0 ||
            contentLength > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Longueur de contenu DAP absente ou invalide.");
        }

        return contentLength;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Corps DAP incomplet.");
            }

            offset += read;
        }
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var completion in _pendingRequests.Values)
        {
            completion.TrySetException(exception);
        }
    }
}
