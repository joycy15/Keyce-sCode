using System.Diagnostics;
using System.IO;

namespace KeycesCode.Languages;

internal static class TypeScriptTooling
{
    public static string FindLoader(string filePath) => FindModuleFile(filePath, "tsx", Path.Combine("dist", "loader.mjs"));

    public static async Task<int> TypeCheckAsync(
        string filePath,
        Action<ProcessOutput> report,
        CancellationToken cancellationToken)
    {
        var compilerPath = FindModuleFile(filePath, "typescript", Path.Combine("lib", "tsc.js"));
        var startInfo = new ProcessStartInfo(ExecutableLocator.Find("node"));
        startInfo.ArgumentList.Add(compilerPath);
        startInfo.ArgumentList.Add("--noEmit");

        var configurationPath = FindConfigurationFile(filePath);
        if (configurationPath is not null)
        {
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(configurationPath);
        }
        else
        {
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add("es2022");
            startInfo.ArgumentList.Add("--module");
            startInfo.ArgumentList.Add("nodenext");
            startInfo.ArgumentList.Add(Path.GetFullPath(filePath));
        }

        return await ExecutionService.RunProcessAsync(
            startInfo, Path.GetDirectoryName(Path.GetFullPath(filePath))!, report, cancellationToken);
    }

    private static string FindModuleFile(string filePath, string packageName, string relativePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (directory is not null)
        {
            var localPath = Path.Combine(directory, "node_modules", packageName, relativePath);
            if (File.Exists(localPath))
            {
                return localPath;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        var executableDirectory = Path.GetDirectoryName(ExecutableLocator.Find("node"));
        if (executableDirectory is not null)
        {
            var installedWithRuntime = Path.Combine(executableDirectory, "node_modules", packageName, relativePath);
            if (File.Exists(installedWithRuntime))
            {
                return installedWithRuntime;
            }
        }

        var globalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules", packageName, relativePath);
        return File.Exists(globalPath)
            ? globalPath
            : throw new FileNotFoundException(
                $"Le paquet Node.js « {packageName} » est introuvable. Installez-le localement ou globalement.");
    }

    private static string? FindConfigurationFile(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "tsconfig.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }
}
