using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KeycesCode.Execution.Interfaces;
using KeycesCode.Execution.Models;

namespace KeycesCode.Execution.Services
{
    public class ToolDetectionService : IToolDetectionService
    {
        // Outils déjà trouvés (python, dotnet...) : on ne relance pas « where » et « --version » à chaque exécution
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ToolInfo> FoundTools =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly (string Id, string Name, string Command, string VersionArg)[] KnownTools =
        {
            ("dotnet", ".NET SDK", "dotnet", "--version"),
            ("python", "Python", "python", "--version"),
            ("node", "Node.js", "node", "--version"),
            ("gcc", "GCC C Compiler", "gcc", "--version"),
            ("g++", "G++ C++ Compiler", "g++", "--version"),
            ("javac", "Java JDK", "javac", "-version"),
            ("git", "Git", "git", "--version")
        };

        private static readonly Dictionary<string, string> ExtensionToToolMap = new(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = "dotnet",
            [".csproj"] = "dotnet",
            [".sln"] = "dotnet",
            [".py"] = "python",
            [".js"] = "node",
            [".mjs"] = "node",
            [".c"] = "gcc",
            [".cpp"] = "g++",
            [".cc"] = "g++",
            [".java"] = "javac"
        };

        public async Task<IReadOnlyList<ToolInfo>> DetectInstalledToolsAsync(CancellationToken cancellationToken = default)
        {
            var results = new List<ToolInfo>();

            foreach (var tool in KnownTools)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = await ProbeToolAsync(tool.Id, tool.Name, tool.Command, tool.VersionArg, cancellationToken);
                results.Add(info);
            }

            return results;
        }

        public async Task<ToolInfo?> DetectToolForFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            string ext = Path.GetExtension(filePath);
            if (!ExtensionToToolMap.TryGetValue(ext, out string? toolId))
                return null;

            var def = KnownTools.FirstOrDefault(t => t.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(def.Id))
                return null;

            if (FoundTools.TryGetValue(def.Id, out ToolInfo? known))
                return known;

            var info = await ProbeToolAsync(def.Id, def.Name, def.Command, def.VersionArg, cancellationToken);
            if (!info.IsAvailable)
                return null;

            FoundTools[def.Id] = info;
            return info;
        }

        public async Task<bool> IsToolAvailableAsync(string toolIdOrCommand, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(toolIdOrCommand))
                return false;

            string? path = await ResolveExecutablePathAsync(toolIdOrCommand.Trim(), cancellationToken);
            return !string.IsNullOrEmpty(path);
        }

        private static async Task<ToolInfo> ProbeToolAsync(
            string id,
            string name,
            string command,
            string versionArg,
            CancellationToken cancellationToken)
        {
            string? resolvedPath = await ResolveExecutablePathAsync(command, cancellationToken);
            if (string.IsNullOrEmpty(resolvedPath))
                return new ToolInfo(id, name, string.Empty, null, false);

            string? version = await ReadToolVersionAsync(resolvedPath, versionArg, cancellationToken);
            return new ToolInfo(id, name, resolvedPath, version, true);
        }

        private static async Task<string?> ResolveExecutablePathAsync(string command, CancellationToken cancellationToken)
        {
            try
            {
                string locator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = locator,
                        Arguments = command,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return null;

                return output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()
                    ?.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> ReadToolVersionAsync(string executablePath, string versionArg, CancellationToken cancellationToken)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = versionArg,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                string raw = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
                return raw
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()
                    ?.Trim();
            }
            catch
            {
                return null;
            }
        }
    }
}
