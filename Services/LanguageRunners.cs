using System.Diagnostics;
using System.ComponentModel;
using System.IO;

namespace Keyce_sCode.Services;

internal sealed class PythonRunner : ILanguageRunner
{
    public bool Supports(string extension) => extension.Equals(".py", StringComparison.OrdinalIgnoreCase);

    public Task RunAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        return RunPythonAsync(filePath, report, cancellationToken);
    }

    private static async Task RunPythonAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        try
        {
            await RunInterpreterAsync("python", filePath, report, cancellationToken);
        }
        catch (Win32Exception)
        {
            await RunInterpreterAsync("python3", filePath, report, cancellationToken);
        }
    }

    private static Task RunInterpreterAsync(
        string interpreter,
        string filePath,
        Action<ProcessOutput> report,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(interpreter);
        startInfo.ArgumentList.Add(filePath);
        return ExecutionService.RunProcessAsync(startInfo, Path.GetDirectoryName(filePath)!, report, cancellationToken);
    }
}

internal sealed class CSharpRunner : ILanguageRunner
{
    public bool Supports(string extension) => extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);

    public async Task RunAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"KeyceCode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            // Un projet temporaire permet de compiler un fichier .cs autonome avec le SDK .NET.
            var sourcePath = Path.Combine(temporaryDirectory, "Program.cs");
            var projectPath = Path.Combine(temporaryDirectory, "Program.csproj");
            await using (var source = File.OpenRead(filePath))
            await using (var destination = File.Create(sourcePath))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }
            await File.WriteAllTextAsync(projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """,
                cancellationToken);

            var startInfo = new ProcessStartInfo("dotnet");
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add("--no-launch-profile");
            await ExecutionService.RunProcessAsync(
                startInfo, Path.GetDirectoryName(filePath)!, report, cancellationToken);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}

internal sealed class NativeCompilerRunner : ILanguageRunner
{
    public bool Supports(string extension) =>
        extension.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".h", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase);

    public async Task RunAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(filePath).Equals(".h", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(filePath).Equals(".hpp", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Un fichier d’en-tête C/C++ ne peut pas être exécuté seul. Ouvrez son fichier source .c ou .cpp.");
        }

        var isCpp = Path.GetExtension(filePath).Equals(".cpp", StringComparison.OrdinalIgnoreCase);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"KeyceCode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var outputPath = Path.Combine(temporaryDirectory, OperatingSystem.IsWindows() ? "program.exe" : "program");
            var compilerInfo = new ProcessStartInfo(isCpp ? "g++" : "gcc");
            compilerInfo.ArgumentList.Add(filePath);
            compilerInfo.ArgumentList.Add("-o");
            compilerInfo.ArgumentList.Add(outputPath);
            var compileExitCode = await ExecutionService.RunProcessAsync(
                compilerInfo, Path.GetDirectoryName(filePath)!, report, cancellationToken);
            if (compileExitCode != 0)
            {
                return;
            }

            var runInfo = new ProcessStartInfo(outputPath);
            await ExecutionService.RunProcessAsync(runInfo, Path.GetDirectoryName(filePath)!, report, cancellationToken);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}

internal sealed class PhpRunner : ILanguageRunner
{
    public bool Supports(string extension) => extension.Equals(".php", StringComparison.OrdinalIgnoreCase);

    public Task RunAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("php");
        startInfo.ArgumentList.Add(filePath);
        return ExecutionService.RunProcessAsync(startInfo, Path.GetDirectoryName(filePath)!, report, cancellationToken);
    }
}

internal sealed class WebRunner : ILanguageRunner
{
    public bool Supports(string extension) =>
        extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".css", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".js", StringComparison.OrdinalIgnoreCase);

    public async Task RunAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Path.GetExtension(filePath).Equals(".js", StringComparison.OrdinalIgnoreCase))
        {
            var startInfo = new ProcessStartInfo("node");
            startInfo.ArgumentList.Add(filePath);
            await ExecutionService.RunProcessAsync(startInfo, Path.GetDirectoryName(filePath)!, report, cancellationToken);
            return;
        }

        var browserInfo = new ProcessStartInfo(filePath) { UseShellExecute = true };
        Process.Start(browserInfo);
        report(new ProcessOutput($"Ouvert dans le navigateur par défaut : {filePath}"));
    }
}
