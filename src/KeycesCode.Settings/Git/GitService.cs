using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using KeycesCode.Core.Abstractions;
using KeycesCode.Core.Models;

namespace KeycesCode.Settings.Git;

public sealed class GitService : IGitService
{
    public async Task<bool> IsRepositoryAsync(string folder)
    {
        if (!Directory.Exists(folder)) return false;

        var result = await RunGitAsync(folder, "rev-parse", "--is-inside-work-tree");
        return result.ExitCode == 0 && result.Output.Trim() == "true";
    }

    public async Task<GitStatus> GetStatusAsync(string folder)
    {
        var result = await RunGitAsync(folder,
            "-c", "core.quotepath=false", "status", "--porcelain=v1", "--branch");
        EnsureSuccess(result, "lire le statut");

        var branch = "";
        var files = new List<GitFileChange>();

        foreach (var rawLine in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("## "))
            {
                branch = ParseBranch(line[3..]);
            }
            else if (line.Length > 3)
            {
                var status = line[..2].Trim();
                var path = line[3..];

                var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0) path = path[(arrow + 4)..];

                files.Add(new GitFileChange(path, status));
            }
        }

        return new GitStatus(branch, files);
    }

    public async Task CommitAsync(string folder, string message, IReadOnlyList<string> files)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Le message de commit est obligatoire.", nameof(message));
        if (files.Count == 0)
            throw new ArgumentException("Aucun fichier à commiter.", nameof(files));

        var addArgs = new List<string> { "add", "--" };
        addArgs.AddRange(files);
        EnsureSuccess(await RunGitAsync(folder, addArgs.ToArray()), "ajouter les fichiers");

        EnsureSuccess(await RunGitAsync(folder, "commit", "-m", message), "faire le commit");
    }

    private static string ParseBranch(string header)
    {
        const string noCommits = "No commits yet on ";
        if (header.StartsWith(noCommits)) return header[noCommits.Length..];

        var dots = header.IndexOf("...", StringComparison.Ordinal);
        if (dots >= 0) return header[..dots];

        var space = header.IndexOf(' ');
        return space >= 0 ? header[..space] : header;
    }

    private static void EnsureSuccess(GitResult result, string action)
    {
        if (result.ExitCode != 0)
            throw new GitException($"Impossible de {action} : {result.Error.Trim()}");
    }

    private static async Task<GitResult> RunGitAsync(string folder, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Win32Exception)
        {
            throw new GitException("Git est introuvable. Installe Git pour Windows.");
        }

        using (process)
        {
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new GitResult(process.ExitCode, await outputTask, await errorTask);
        }
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
