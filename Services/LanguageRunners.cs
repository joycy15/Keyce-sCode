using System.Diagnostics;
using System.ComponentModel;
using System.IO;

namespace Keyce_sCode.Services;

internal sealed class PythonRunner : ILanguageRunner
{
    public bool Supports(string extension) => extension.Equals(".py", StringComparison.OrdinalIgnoreCase);

    public Task<int> RunAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        return RunPythonAsync(filePath, report, cancellationToken);
    }

    private static async Task<int> RunPythonAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        try
        {
            return await RunInterpreterAsync("python", filePath, report, cancellationToken);
        }
        catch (Win32Exception)
        {
            return await RunInterpreterAsync("python3", filePath, report, cancellationToken);
        }
    }

    private static Task<int> RunInterpreterAsync(
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
    public bool Supports(string extension) =>
        extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase);

    public async Task<int> RunAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        var existingProject = FindProjectFile(filePath);
        if (existingProject is not null)
        {
            var projectInfo = new ProcessStartInfo("dotnet");
            projectInfo.ArgumentList.Add("run");
            projectInfo.ArgumentList.Add("--project");
            projectInfo.ArgumentList.Add(existingProject);
            return await ExecutionService.RunProcessAsync(
                projectInfo, Path.GetDirectoryName(existingProject)!, report, cancellationToken);
        }

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
            return await ExecutionService.RunProcessAsync(
                startInfo, Path.GetDirectoryName(filePath)!, report, cancellationToken);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    internal static string? FindProjectFile(string filePath)
    {
        if (Path.GetExtension(filePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(filePath);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (directory is not null)
        {
            var projects = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
            if (projects.Length == 1)
            {
                return projects[0];
            }

            if (projects.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Plusieurs projets .NET se trouvent dans « {directory} ». Ouvrez le fichier .csproj à exécuter.");
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }
}

internal sealed class NativeCompilerRunner : ILanguageRunner
{
    public bool Supports(string extension) =>
        extension.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cc", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cxx", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".h", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".hh", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".hxx", StringComparison.OrdinalIgnoreCase);

    public async Task<int> RunAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".h", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".hh", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".hxx", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Un fichier d’en-tête C/C++ ne peut pas être exécuté seul. Ouvrez son fichier source .c ou .cpp.");
        }

        var isCpp = extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".cc", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".cxx", StringComparison.OrdinalIgnoreCase);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"KeyceCode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var outputPath = Path.Combine(temporaryDirectory, OperatingSystem.IsWindows() ? "program.exe" : "program");
            var compilerInfo = new ProcessStartInfo(ExecutableLocator.Find(isCpp ? "g++" : "gcc"));
            compilerInfo.ArgumentList.Add(filePath);
            compilerInfo.ArgumentList.Add("-o");
            compilerInfo.ArgumentList.Add(outputPath);
            var compileExitCode = await ExecutionService.RunProcessAsync(
                compilerInfo, Path.GetDirectoryName(filePath)!, report, cancellationToken);
            if (compileExitCode != 0)
            {
                return compileExitCode;
            }

            var runInfo = new ProcessStartInfo(outputPath);
            return await ExecutionService.RunProcessAsync(
                runInfo, Path.GetDirectoryName(filePath)!, report, cancellationToken);
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

    public Task<int> RunAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ExecutableLocator.Find("php"));
        startInfo.ArgumentList.Add(filePath);
        return ExecutionService.RunProcessAsync(startInfo, Path.GetDirectoryName(filePath)!, report, cancellationToken);
    }
}

internal sealed class WebRunner : ILanguageRunner
{
    public bool Supports(string extension) =>
        extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".css", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mts", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cts", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase);

    public async Task<int> RunAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase))
        {
            var startInfo = new ProcessStartInfo(ExecutableLocator.Find("node"));
            if (extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".mts", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".cts", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase))
            {
                var typeCheckExitCode = await TypeScriptTooling.TypeCheckAsync(filePath, report, cancellationToken);
                if (typeCheckExitCode != 0)
                {
                    return typeCheckExitCode;
                }

                startInfo.ArgumentList.Add("--import");
                startInfo.ArgumentList.Add(new Uri(TypeScriptTooling.FindLoader(filePath)).AbsoluteUri);
            }

            startInfo.ArgumentList.Add(filePath);
            return await ExecutionService.RunProcessAsync(
                startInfo, Path.GetDirectoryName(filePath)!, report, cancellationToken);
        }

        var browserInfo = new ProcessStartInfo(filePath) { UseShellExecute = true };
        Process.Start(browserInfo);
        report(new ProcessOutput($"Ouvert dans le navigateur par défaut : {filePath}"));
        return 0;
    }

}
