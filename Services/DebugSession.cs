using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security;

namespace Keyce_sCode.Services;

internal sealed class DebugSession : IDisposable
{
    private readonly IDebugClient _client;
    private readonly string? _temporaryDirectory;
    private readonly Action<ProcessOutput> _report;
    private bool _stopped;

    private DebugSession(
        IDebugClient client,
        string? temporaryDirectory,
        Action<ProcessOutput> report)
    {
        _client = client;
        _temporaryDirectory = temporaryDirectory;
        _report = report;
    }

    public event Action<JsonElement>? EventReceived
    {
        add => _client.EventReceived += value;
        remove => _client.EventReceived -= value;
    }

    public Task<JsonElement> RequestAsync(string command, object? arguments, CancellationToken cancellationToken) =>
        _client.RequestAsync(command, arguments, cancellationToken);

    public static async Task<DebugSession> StartAsync(
        string filePath,
        IReadOnlyDictionary<string, IReadOnlyCollection<int>> breakpoints,
        Action<ProcessOutput> report,
        Action<JsonElement> eventReceived,
        CancellationToken cancellationToken)
    {
        IDebugClient? client = null;
        string? temporaryDirectory = null;
        var sessionStarted = false;
        try
        {
            var target = await CreateTargetAsync(filePath, report, cancellationToken);
            temporaryDirectory = target.TemporaryDirectory;
            client = target.NodeProgram is null
                ? DebugAdapterClient.Start(target.AdapterStartInfo, report)
                : await NodeInspectorClient.StartAsync(
                    target.NodeExecutable!,
                    target.NodeProgram,
                    target.NodeRuntimeArguments!,
                    target.AdapterStartInfo.WorkingDirectory,
                    report,
                    cancellationToken);
            client.EventReceived += eventReceived;

            var initialized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.EventReceived += message =>
            {
                if (message.TryGetProperty("event", out var eventName) &&
                    eventName.GetString() == "initialized")
                {
                    initialized.TrySetResult(true);
                }
            };

            await client.RequestAsync("initialize", new
            {
                clientID = "Keyce-sCode",
                clientName = "Keyce's Code",
                adapterID = target.AdapterId,
                locale = "fr",
                linesStartAt1 = true,
                columnsStartAt1 = true,
                pathFormat = "path",
                supportsVariableType = true,
                supportsRunInTerminalRequest = false
            }, cancellationToken);
            var launchTask = client.RequestAsync("launch", target.LaunchArguments, cancellationToken);
            if (await Task.WhenAny(initialized.Task, launchTask) == launchTask)
            {
                await launchTask;
            }

            await initialized.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            foreach (var (sourcePath, lines) in breakpoints)
            {
                if (lines.Count == 0 || !File.Exists(sourcePath))
                {
                    continue;
                }

                await client.RequestAsync("setBreakpoints", new
                {
                    source = new { name = Path.GetFileName(sourcePath), path = Path.GetFullPath(sourcePath) },
                    breakpoints = lines.Order().Select(line => new { line }).ToArray(),
                    sourceModified = false
                }, cancellationToken);
            }

            await client.RequestAsync("configurationDone", new { }, cancellationToken);
            await launchTask;
            sessionStarted = true;
            return new DebugSession(client, temporaryDirectory, report);
        }
        finally
        {
            if (!sessionStarted)
            {
                client?.Dispose();
                DeleteTemporaryDirectory(temporaryDirectory, report);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        try
        {
            await _client.StopAsync(cancellationToken);
        }
        finally
        {
            DeleteTemporaryDirectory(_temporaryDirectory, _report);
        }
    }

    public void Dispose()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _client.Dispose();
        DeleteTemporaryDirectory(_temporaryDirectory, _report);
    }

    private static async Task<DebugTarget> CreateTargetAsync(
        string filePath,
        Action<ProcessOutput> report,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return await CreateCSharpTargetAsync(filePath, report, cancellationToken);
        }

        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            return CreatePythonTarget(filePath);
        }

        if (extension.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cc", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cxx", StringComparison.OrdinalIgnoreCase))
        {
            return await CreateNativeTargetAsync(filePath, report, cancellationToken);
        }

        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase))
        {
            if (extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mts", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".cts", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase))
            {
                var typeCheckExitCode = await TypeScriptTooling.TypeCheckAsync(filePath, report, cancellationToken);
                if (typeCheckExitCode != 0)
                {
                    throw new InvalidOperationException($"La vérification TypeScript a échoué (code {typeCheckExitCode}).");
                }
            }

            return CreateNodeTarget(filePath);
        }

        if (extension.Equals(".php", StringComparison.OrdinalIgnoreCase))
        {
            return CreatePhpTarget(filePath);
        }

        throw new NotSupportedException($"Le débogage des fichiers « {extension} » n’est pas pris en charge.");
    }

    private static async Task<DebugTarget> CreateCSharpTargetAsync(
        string filePath,
        Action<ProcessOutput> report,
        CancellationToken cancellationToken)
    {
        string? temporaryDirectory = null;
        var targetCreated = false;
        try
        {
            string projectPath;
            var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
            if (Path.GetExtension(filePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                projectPath = Path.GetFullPath(filePath);
                workingDirectory = Path.GetDirectoryName(projectPath)!;
            }
            else
            {
                var existingProject = CSharpRunner.FindProjectFile(filePath);
                if (existingProject is not null)
                {
                    projectPath = existingProject;
                    workingDirectory = Path.GetDirectoryName(projectPath)!;
                }
                else
                {
                    temporaryDirectory = CreateTemporaryDirectory("csharp");
                    projectPath = Path.Combine(temporaryDirectory, "Program.csproj");
                    var escapedSourcePath = SecurityElement.Escape(Path.GetFullPath(filePath));
                    await File.WriteAllTextAsync(projectPath,
                        $"""
                        <Project Sdk="Microsoft.NET.Sdk">
                          <PropertyGroup>
                            <OutputType>Exe</OutputType>
                            <TargetFramework>net8.0</TargetFramework>
                            <ImplicitUsings>enable</ImplicitUsings>
                            <Nullable>enable</Nullable>
                            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                          </PropertyGroup>
                          <ItemGroup>
                            <Compile Include="{escapedSourcePath}" />
                          </ItemGroup>
                        </Project>
                        """,
                        cancellationToken);
                }
            }

            var buildInfo = new ProcessStartInfo("dotnet");
            buildInfo.ArgumentList.Add("build");
            buildInfo.ArgumentList.Add(projectPath);
            buildInfo.ArgumentList.Add("--configuration");
            buildInfo.ArgumentList.Add("Debug");
            buildInfo.ArgumentList.Add("--nologo");
            var exitCode = await ExecutionService.RunProcessAsync(
                buildInfo, workingDirectory, report, cancellationToken);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"La compilation du projet .NET a échoué (code {exitCode}).");
            }

            var targetPathInfo = new ProcessStartInfo("dotnet");
            targetPathInfo.ArgumentList.Add("msbuild");
            targetPathInfo.ArgumentList.Add(projectPath);
            targetPathInfo.ArgumentList.Add("-nologo");
            targetPathInfo.ArgumentList.Add("-getProperty:TargetPath");
            var programPath = await RunAndCaptureOutputAsync(
                targetPathInfo, workingDirectory, cancellationToken);
            if (!File.Exists(programPath))
            {
                throw new FileNotFoundException($"Le fichier de démarrage .NET est introuvable : {programPath}");
            }

            var adapterPath = FindNetCoreDebugger();
            var adapterInfo = new ProcessStartInfo(adapterPath);
            adapterInfo.ArgumentList.Add("--interpreter=vscode");
            adapterInfo.WorkingDirectory = workingDirectory;
            var target = new DebugTarget("coreclr", adapterInfo, new
            {
                type = "coreclr",
                request = "launch",
                name = Path.GetFileNameWithoutExtension(programPath),
                program = programPath,
                cwd = workingDirectory,
                console = "internalConsole",
                stopAtEntry = true,
                justMyCode = true
            }, temporaryDirectory);
            targetCreated = true;
            return target;
        }
        finally
        {
            if (!targetCreated)
            {
                DeleteTemporaryDirectory(temporaryDirectory, report);
            }
        }
    }

    private static DebugTarget CreatePythonTarget(string filePath)
    {
        var pythonPath = ExecutableLocator.Find("python");
        var extensionDirectory = FindExtensionDirectory("ms-python.debugpy-*");
        var librariesPath = Path.Combine(extensionDirectory, "bundled", "libs");
        if (!Directory.Exists(Path.Combine(librariesPath, "debugpy")))
        {
            throw new DirectoryNotFoundException($"Le paquet debugpy est introuvable dans « {librariesPath} ».");
        }

        var adapterInfo = new ProcessStartInfo(pythonPath);
        adapterInfo.ArgumentList.Add("-m");
        adapterInfo.ArgumentList.Add("debugpy.adapter");
        adapterInfo.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var existingPythonPath = adapterInfo.Environment.TryGetValue("PYTHONPATH", out var value) ? value : null;
        adapterInfo.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existingPythonPath)
            ? librariesPath
            : librariesPath + Path.PathSeparator + existingPythonPath;

        return new DebugTarget("python", adapterInfo, new
        {
            type = "python",
            request = "launch",
            name = Path.GetFileName(filePath),
            program = Path.GetFullPath(filePath),
            cwd = Path.GetDirectoryName(Path.GetFullPath(filePath)),
            console = "internalConsole",
            stopOnEntry = true,
            justMyCode = true
        }, null);
    }

    private static async Task<DebugTarget> CreateNativeTargetAsync(
        string filePath,
        Action<ProcessOutput> report,
        CancellationToken cancellationToken)
    {
        var isCpp = Path.GetExtension(filePath).ToLowerInvariant() is ".cpp" or ".cc" or ".cxx";
        var compiler = ExecutableLocator.Find(isCpp ? "g++" : "gcc");
        var debugger = ExecutableLocator.Find("gdb");
        var adapterPath = FindExtensionFile(
            "ms-vscode.cpptools-*",
            Path.Combine("debugAdapters", "bin", "OpenDebugAD7.exe"));
        var temporaryDirectory = CreateTemporaryDirectory("native");
        var targetCreated = false;
        try
        {
            var workingDirectory = GetShortPath(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            var outputPath = Path.Combine(GetShortPath(temporaryDirectory), "program.exe");
            var compilerInfo = new ProcessStartInfo(compiler);
            compilerInfo.ArgumentList.Add("-g");
            compilerInfo.ArgumentList.Add("-O0");
            compilerInfo.ArgumentList.Add(Path.GetFullPath(filePath));
            compilerInfo.ArgumentList.Add("-o");
            compilerInfo.ArgumentList.Add(outputPath);
            var compilerPath = compilerInfo.Environment.TryGetValue("PATH", out var existingCompilerPath)
                ? existingCompilerPath
                : string.Empty;
            compilerInfo.Environment["PATH"] = Path.GetDirectoryName(compiler) + Path.PathSeparator + compilerPath;
            var exitCode = await ExecutionService.RunProcessAsync(
                compilerInfo, workingDirectory, report, cancellationToken);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"La compilation C/C++ a échoué (code {exitCode}).");
            }

            var adapterInfo = new ProcessStartInfo(adapterPath)
            {
                WorkingDirectory = workingDirectory
            };
            var existingPath = adapterInfo.Environment.TryGetValue("PATH", out var pathValue) ? pathValue : string.Empty;
            adapterInfo.Environment["PATH"] = Path.GetDirectoryName(debugger) + Path.PathSeparator + existingPath;
            var target = new DebugTarget("cppdbg", adapterInfo, new
            {
                type = "cppdbg",
                request = "launch",
                name = Path.GetFileName(filePath),
                program = outputPath,
                cwd = workingDirectory,
                MIMode = "gdb",
                miDebuggerPath = debugger,
                externalConsole = false,
                stopAtEntry = true
            }, temporaryDirectory);
            targetCreated = true;
            return target;
        }
        finally
        {
            if (!targetCreated)
            {
                DeleteTemporaryDirectory(temporaryDirectory, report);
            }
        }
    }

    private static DebugTarget CreateNodeTarget(string filePath)
    {
        var nodePath = ExecutableLocator.Find("node");
        var adapterInfo = new ProcessStartInfo(nodePath);
        adapterInfo.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var runtimeArguments = Path.GetExtension(filePath).ToLowerInvariant() is ".ts" or ".mts" or ".cts" or ".tsx"
            ? new[] { "--import", new Uri(TypeScriptTooling.FindLoader(filePath)).AbsoluteUri }
            : Array.Empty<string>();
        var launchArguments = new Dictionary<string, object?>
        {
            ["type"] = "pwa-node",
            ["request"] = "launch",
            ["name"] = Path.GetFileName(filePath),
            ["program"] = Path.GetFullPath(filePath),
            ["cwd"] = Path.GetDirectoryName(Path.GetFullPath(filePath)),
            ["runtimeExecutable"] = nodePath,
            ["console"] = "internalConsole",
            ["stopOnEntry"] = true
        };
        return new DebugTarget(
            "pwa-node", adapterInfo, launchArguments, null, nodePath, Path.GetFullPath(filePath), runtimeArguments);
    }

    private static DebugTarget CreatePhpTarget(string filePath)
    {
        var phpPath = ExecutableLocator.Find("php");
        var nodePath = ExecutableLocator.Find("node");
        var adapterPath = FindExtensionFile("xdebug.php-debug-*", Path.Combine("out", "phpDebug.js"));
        var iniDirectory = EnsurePhpDebugConfiguration(phpPath);
        var adapterInfo = new ProcessStartInfo(nodePath);
        adapterInfo.ArgumentList.Add(adapterPath);
        adapterInfo.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        adapterInfo.Environment["PHPRC"] = iniDirectory;
        return new DebugTarget("php", adapterInfo, new
        {
            type = "php",
            request = "launch",
            name = Path.GetFileName(filePath),
            program = Path.GetFullPath(filePath),
            cwd = Path.GetDirectoryName(Path.GetFullPath(filePath)),
            runtimeExecutable = phpPath,
            runtimeArgs = Array.Empty<string>(),
            env = new Dictionary<string, string>
            {
                ["XDEBUG_MODE"] = "debug",
                ["XDEBUG_CONFIG"] = "client_host=127.0.0.1 client_port=9003"
            },
            port = 9003,
            stopOnEntry = true
        }, null);
    }

    private static string EnsurePhpDebugConfiguration(string phpPath)
    {
        var extension = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Keyce-sCode", "php", "php_xdebug.dll");
        if (!File.Exists(extension))
        {
            throw new FileNotFoundException(
                "Xdebug pour PHP 8.4 n’est pas installé. Installez php_xdebug.dll dans le dossier utilisateur Keyce-sCode/php.");
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Keyce-sCode", "php");
        Directory.CreateDirectory(directory);

        var phpDirectory = Path.GetDirectoryName(phpPath)
            ?? throw new InvalidOperationException("Impossible de trouver le dossier du runtime PHP.");
        var iniContents = string.Join(Environment.NewLine,
        [
            $"zend_extension=\"{GetShortPath(extension)}\"",
            $"extension_dir=\"{GetShortPath(Path.Combine(phpDirectory, "ext"))}\"",
            "xdebug.mode=debug",
            "xdebug.start_with_request=yes",
            "xdebug.client_host=127.0.0.1",
            "xdebug.client_port=9003"
        ]) + Environment.NewLine;
        File.WriteAllText(
            Path.Combine(directory, "php.ini"),
            iniContents,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return GetShortPath(directory);
    }

    private static string GetShortPath(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(path, buffer, buffer.Capacity);
        return length == 0 || length >= buffer.Capacity
            ? throw new IOException($"Impossible de préparer un chemin Windows compatible : {path}")
            : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);

    private static async Task<string> RunAndCaptureOutputAsync(
        ProcessStartInfo startInfo,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Impossible de démarrer « {startInfo.FileName} ».");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"La recherche du fichier de démarrage .NET a échoué : {error.Trim()}");
        }

        return output.Trim();
    }

    private static string FindExtensionFile(string pattern, string relativePath)
    {
        var directory = FindExtensionDirectory(pattern);
        var path = Path.Combine(directory, relativePath);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Le fichier d’adaptateur est introuvable : {path}");
    }

    private static string FindNetCoreDebugger()
    {
        var debuggerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Keyce-sCode", "debuggers", "netcoredbg-3.2.0-1092", "netcoredbg", "netcoredbg.exe");
        return File.Exists(debuggerPath)
            ? GetShortPath(debuggerPath)
            : throw new FileNotFoundException(
                "Le débogueur .NET netcoredbg est introuvable. Installez la version 3.2.0-1092 dans le dossier utilisateur Keyce-sCode/debuggers.");
    }

    private static string FindExtensionDirectory(string pattern) =>
        TryFindExtensionDirectory(pattern)
        ?? throw new DirectoryNotFoundException($"L’extension de débogage « {pattern} » n’est pas installée.");

    private static string? TryFindExtensionDirectory(string pattern)
    {
        var extensionsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        return Directory.Exists(extensionsPath)
            ? Directory.GetDirectories(extensionsPath, pattern)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    private static string CreateTemporaryDirectory(string language)
    {
        var path = Path.Combine(Path.GetTempPath(), $"KeyceCode-Debug-{language}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string? path, Action<ProcessOutput> report)
    {
        if (path is null || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException exception)
        {
            report(new ProcessOutput($"Nettoyage du dossier temporaire : {exception.Message}", true));
        }
        catch (UnauthorizedAccessException exception)
        {
            report(new ProcessOutput($"Nettoyage du dossier temporaire : {exception.Message}", true));
        }
    }

    private sealed record DebugTarget(
        string AdapterId,
        ProcessStartInfo AdapterStartInfo,
        object LaunchArguments,
        string? TemporaryDirectory,
        string? NodeExecutable = null,
        string? NodeProgram = null,
        IReadOnlyList<string>? NodeRuntimeArguments = null);
}
