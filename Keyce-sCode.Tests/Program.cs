using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using System.IO;
using System.Text.Json;
using System.Text;
using Keyce_sCode.Services;

var mode = args.Length > 0 ? args[0] : "node";
if (mode == "editor")
{
    await RunEditorSmokeTestsAsync();
    return;
}

if (mode is "csharp" or "python" or "cpp" or "php")
{
    await RunAdapterSmokeTestAsync(mode);
    return;
}

if (mode == "typescript")
{
    await RunTypeScriptSmokeTestAsync();
    return;
}

if (mode != "node")
{
    throw new ArgumentException("Choose a smoke-test mode: node, csharp, python, cpp, php, typescript, or editor.");
}

var nodePath = args.Length > 1 ? args[1] : "node";
var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"KeyceCode-DapTest-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporaryDirectory);
var sourcePath = Path.Combine(temporaryDirectory, "breakpoint.js");
await File.WriteAllLinesAsync(sourcePath,
[
    "let count = 1;",
    "count += 2;",
    "console.log(count);"
]);

var stoppedEvents = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>(new System.Threading.Channels
    .UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
var output = new List<ProcessOutput>();
NodeInspectorClient? client = null;
try
{
    client = await NodeInspectorClient.StartAsync(
        nodePath,
        sourcePath,
        Array.Empty<string>(),
        temporaryDirectory,
        item =>
        {
            lock (output)
            {
                output.Add(item);
            }
        },
        CancellationToken.None);
    client.EventReceived += message =>
    {
        if (message.TryGetProperty("event", out var eventName) &&
            eventName.GetString() == "stopped")
        {
            stoppedEvents.Writer.TryWrite(message.Clone());
        }
    };

    await client.RequestAsync("initialize", new { }, CancellationToken.None);
    await client.RequestAsync("launch", new { }, CancellationToken.None);
    await client.RequestAsync("setBreakpoints", new
    {
        source = new { name = Path.GetFileName(sourcePath), path = sourcePath },
        breakpoints = new[] { new { line = 2 } }
    }, CancellationToken.None);
    await client.RequestAsync("configurationDone", new { }, CancellationToken.None);

    await stoppedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
    await client.RequestAsync("continue", new { threadId = 1 }, CancellationToken.None);
    await stoppedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
    var stackTrace = await client.RequestAsync("stackTrace", new { threadId = 1, levels = 10 }, CancellationToken.None);
    var frame = stackTrace.GetProperty("body").GetProperty("stackFrames")[0];
    if (frame.GetProperty("line").GetInt32() != 2)
    {
        throw new InvalidOperationException("The JavaScript debugger stopped on the wrong breakpoint line.");
    }

    var frameId = frame.GetProperty("id").GetInt32();
    var scopesResponse = await client.RequestAsync("scopes", new { frameId }, CancellationToken.None);
    var localScope = scopesResponse.GetProperty("body").GetProperty("scopes")
        .EnumerateArray()
        .First(scope => scope.GetProperty("name").GetString() == "local");
    var variablesResponse = await client.RequestAsync("variables", new
    {
        variablesReference = localScope.GetProperty("variablesReference").GetInt32()
    }, CancellationToken.None);
    var count = variablesResponse.GetProperty("body").GetProperty("variables")
        .EnumerateArray()
        .First(variable => variable.GetProperty("name").GetString() == "count")
        .GetProperty("value").GetString();
    if (count != "1")
    {
        throw new InvalidOperationException($"Expected count=1 before the breakpoint, received {count}.");
    }

    await client.RequestAsync("next", new { threadId = 1 }, CancellationToken.None);
    await stoppedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
    var steppedFrame = (await client.RequestAsync(
            "stackTrace", new { threadId = 1, levels = 10 }, CancellationToken.None))
        .GetProperty("body").GetProperty("stackFrames")[0];
    if (steppedFrame.GetProperty("line").GetInt32() != 3)
    {
        throw new InvalidOperationException("Stepping over the breakpoint did not advance one source line.");
    }

    await client.RequestAsync("disconnect", new { terminateDebuggee = true }, CancellationToken.None);
    client = null;
    Console.WriteLine("PASS: Node.js DAP stops at a breakpoint, inspects locals, steps, and disconnects.");
}
catch
{
    lock (output)
    {
        foreach (var item in output)
        {
            Console.Error.WriteLine(item.Text);
        }
    }

    throw;
}
finally
{
    if (client is not null)
    {
        await client.StopAsync(CancellationToken.None);
    }

    if (Directory.Exists(temporaryDirectory))
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
}

static async Task RunAdapterSmokeTestAsync(string language)
{
    var extension = language switch
    {
        "csharp" => ".cs",
        "python" => ".py",
        "cpp" => ".cpp",
        "php" => ".php",
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };
    var source = language switch
    {
        "csharp" => "int count = 1;\nint result = count + 2;\nConsole.WriteLine(result);",
        "python" => "count = 1\nresult = count + 2\nprint(result)",
        "cpp" => "int main() {\n    int count = 1;\n    int result = count + 2;\n    return result;\n}",
        "php" => "<?php\n$count = 1;\n$result = $count + 2;\necho $result . PHP_EOL;",
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };
    var directory = Path.Combine(Path.GetTempPath(), $"KeyceCode-{language}-DapTest-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var filePath = Path.Combine(directory, "breakpoint" + extension);
    await File.WriteAllTextAsync(filePath, source);

    var stoppedEvents = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>(new System.Threading.Channels
        .UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    var output = new List<ProcessOutput>();
    DebugSession? session = null;
    try
    {
        session = await DebugSession.StartAsync(
            filePath,
            new Dictionary<string, IReadOnlyCollection<int>>
            {
                [Path.GetFullPath(filePath)] = new[] { 2 }
            },
            item =>
            {
                lock (output)
                {
                    output.Add(item);
                }
            },
            message =>
            {
                if (message.TryGetProperty("event", out var eventName) &&
                    eventName.GetString() == "stopped")
                {
                    stoppedEvents.Writer.TryWrite(message.Clone());
                }
            },
            CancellationToken.None);

        var stopEvent = await stoppedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        var threadId = stopEvent.GetProperty("body").GetProperty("threadId").GetInt32();
        var stackTrace = await session.RequestAsync("stackTrace", new { threadId, levels = 10 }, CancellationToken.None);
        var frames = stackTrace.GetProperty("body").GetProperty("stackFrames");
        if (frames.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"The {language} debugger did not return a stack frame.");
        }

        if (frames[0].GetProperty("line").GetInt32() != 2)
        {
            await session.RequestAsync("continue", new { threadId }, CancellationToken.None);
            await stoppedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            stackTrace = await session.RequestAsync("stackTrace", new { threadId, levels = 10 }, CancellationToken.None);
            frames = stackTrace.GetProperty("body").GetProperty("stackFrames");
            if (frames.GetArrayLength() == 0 || frames[0].GetProperty("line").GetInt32() != 2)
            {
                throw new InvalidOperationException($"The {language} debugger did not stop on source line 2.");
            }
        }

        await session.RequestAsync("next", new { threadId }, CancellationToken.None);
        await stoppedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Console.WriteLine($"PASS: {language} DAP attaches, hits a line breakpoint, steps, and exposes the stack.");
    }
    catch
    {
        lock (output)
        {
            foreach (var item in output)
            {
                Console.Error.WriteLine(item.Text);
            }
        }

        throw;
    }
    finally
    {
        if (session is not null)
        {
            await session.StopAsync(CancellationToken.None);
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static async Task RunTypeScriptSmokeTestAsync()
{
    var directory = Path.Combine(Path.GetTempPath(), $"KeyceCode-TypeScriptTest-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var filePath = Path.Combine(directory, "program.ts");
    await File.WriteAllTextAsync(filePath, "const answer: number = 42;\nconsole.log(answer);");
    var output = new List<ProcessOutput>();
    try
    {
        var exitCode = await new ExecutionService().RunAsync(
            filePath,
            item =>
            {
                lock (output)
                {
                    output.Add(item);
                }
            },
            CancellationToken.None);
        if (exitCode != 0 || !output.Any(item => item.Text == "42"))
        {
            throw new InvalidOperationException("TypeScript type-checking or execution produced an unexpected result.");
        }

        Console.WriteLine("PASS: TypeScript type-checks and executes in Node.js.");
    }
    catch
    {
        lock (output)
        {
            foreach (var item in output)
            {
                Console.Error.WriteLine(item.Text);
            }
        }

        throw;
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static async Task RunEditorSmokeTestsAsync()
{
    var highlighter = new LanguageSyntaxHighlighter();
    var tokenCases = new[]
    {
        (Extension: ".cs", Source: "class Sample { string value = \"x\"; // comment", ExpectedColor: "ReferenceTypeKeywords"),
        (Extension: ".py", Source: "def greet(): return \"x\" # comment", ExpectedColor: "Keywords"),
        (Extension: ".js", Source: "const answer = \"42\"; // comment", ExpectedColor: "JavaScriptKeyWords"),
        (Extension: ".css", Source: "body { color: \"red\"; /* comment */ }", ExpectedColor: "Selector")
    };
    foreach (var (extension, source, expectedColor) in tokenCases)
    {
        var definition = highlighter.GetDefinition("sample" + extension, isDarkTheme: true)
            ?? throw new InvalidOperationException($"No built-in syntax definition was found for {extension}.");
        using var tokenizer = new DocumentHighlighter(new TextDocument(source), definition);
        var colors = tokenizer.HighlightLine(1).Sections
            .Select(section => section.Color.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!colors.Contains(expectedColor) || !colors.Contains("Comment") || !colors.Contains("String"))
        {
            throw new InvalidOperationException(
                $"{extension} did not tokenize keywords/selectors, strings, and comments: {string.Join(", ", colors)}.");
        }
    }

    var html = highlighter.GetDefinition("sample.html", isDarkTheme: true)
        ?? throw new InvalidOperationException("No HTML syntax definition was found.");
    if (html.GetNamedRuleSet("CSSSet") is null || html.GetNamedRuleSet("JavaScriptSet") is null)
    {
        throw new InvalidOperationException("HTML embedded CSS and JavaScript rules were not loaded.");
    }

    var htmlDocument = new TextDocument(
        "<style>\nbody { color: red; }\n</style>\n<script>\nconst answer = 42;\n</script>");
    using (var documentHighlighter = new DocumentHighlighter(htmlDocument, html))
    {
        var cssColors = documentHighlighter.HighlightLine(2).Sections
            .Select(section => section.Color.Name)
            .ToHashSet(StringComparer.Ordinal);
        var javascriptColors = documentHighlighter.HighlightLine(5).Sections
            .Select(section => section.Color.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!cssColors.Contains("Selector") || !javascriptColors.Contains("JavaScriptKeyWords"))
        {
            throw new InvalidOperationException("HTML style/script contents were not tokenized as CSS and JavaScript.");
        }
    }

    var directory = Path.Combine(Path.GetTempPath(), $"KeyceCode-EditorTest-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "utf8.txt");
    const string content = "Éditeur UTF-8 — café 🙂";
    var service = new FileService();
    try
    {
        await service.SaveAsync(path, content);
        var savedBytes = await File.ReadAllBytesAsync(path);
        if (savedBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            throw new InvalidOperationException("The UTF-8 save unexpectedly wrote a byte-order mark.");
        }

        if (await service.ReadAllTextAsync(path) != content)
        {
            throw new InvalidOperationException("The UTF-8 file round-trip changed its contents.");
        }

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        if (await service.ReadAllTextAsync(path) != content)
        {
            throw new InvalidOperationException("The file reader did not detect a UTF-8 byte-order mark.");
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await service.SaveAsync(path, "must not replace the target", cancellation.Token);
            throw new InvalidOperationException("A cancelled save unexpectedly completed.");
        }
        catch (OperationCanceledException)
        {
        }

        if (await service.ReadAllTextAsync(path) != content ||
            Directory.EnumerateFiles(directory, "*.tmp").Any())
        {
            throw new InvalidOperationException("A cancelled save changed the target or left a temporary file.");
        }

        await File.WriteAllBytesAsync(path, new byte[] { 0xC3, 0x28 });
        try
        {
            await service.ReadAllTextAsync(path);
            throw new InvalidOperationException("Invalid UTF-8 bytes were silently accepted.");
        }
        catch (DecoderFallbackException)
        {
        }

        Console.WriteLine("PASS: syntax definitions and embedded HTML CSS/JavaScript load; UTF-8 saves are atomic.");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}
