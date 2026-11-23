using System.IO;

namespace KeycesCode.Languages;

internal static class ExecutableLocator
{
    public static string Find(string name)
    {
        var executableName = Path.HasExtension(name) ? name : name + ".exe";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var candidate in GetKnownPaths(executableName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"L’outil « {name} » n’est pas installé ou absent du PATH.");
    }

    private static IEnumerable<string> GetKnownPaths(string executableName)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var executable = executableName.ToLowerInvariant();
        if (executable == "node.exe")
        {
            yield return Path.Combine(
                localAppData, "Programs", "nodejs-26.13.1", "node-v26.13.1-win-x64", executableName);
        }

        if (executable == "php.exe")
        {
            var packagesDirectory = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packagesDirectory))
            {
                foreach (var packageDirectory in Directory.GetDirectories(packagesDirectory, "PHP.PHP.*"))
                {
                    yield return Path.Combine(packageDirectory, executableName);
                }
            }
        }

        if (executable is "gcc.exe" or "g++.exe" or "gdb.exe")
        {
            yield return Path.Combine(@"C:\msys64\ucrt64\bin", executableName);
        }
    }
}
