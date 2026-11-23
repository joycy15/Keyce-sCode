using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using KeycesCode.Core.Abstractions;
using KeycesCode.Core.Models;

namespace KeycesCode.Settings.Git;

public sealed class GitService : IGitService
{
    // ========== Dépôt local ==========

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

    // ========== GitHub (dépôt distant) ==========

    public async Task<string?> GetRemoteUrlAsync(string folder)
    {
        var result = await RunGitAsync(folder, "remote", "get-url", "origin");
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    public async Task SetRemoteUrlAsync(string folder, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("L'URL du dépôt est obligatoire.", nameof(url));

        url = url.Trim();
        var existing = await GetRemoteUrlAsync(folder);

        // Pas encore d'URL : on l'ajoute. Sinon : on la remplace.
        var result = existing is null
            ? await RunGitAsync(folder, "remote", "add", "origin", url)
            : await RunGitAsync(folder, "remote", "set-url", "origin", url);

        EnsureSuccess(result, "enregistrer l'URL du dépôt");
    }

    public async Task<string> PushAsync(string folder)
    {
        var branch = (await GetStatusAsync(folder)).Branch;

        var result = await RunGitAsync(folder, "push", "-u", "origin", branch);
        EnsureSuccess(result, "envoyer vers GitHub (push)");

        return $"Push réussi vers origin/{branch}.";
    }

    public async Task<string> PullAsync(string folder)
    {
        var result = await RunGitAsync(folder, "pull");
        EnsureSuccess(result, "récupérer depuis GitHub (pull)");

        var firstLine = result.Output.Split('\n')[0].Trim();
        return firstLine.Length == 0 ? "Pull terminé." : $"Pull terminé : {firstLine}";
    }

    public async Task<string> CloneAsync(string url, string parentFolder)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("L'URL du dépôt est obligatoire.", nameof(url));
        if (!Directory.Exists(parentFolder))
            throw new GitException($"Le dossier {parentFolder} n'existe pas.");

        var name = RepositoryNameFromUrl(url.Trim());
        var target = Path.Combine(parentFolder, name);

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new GitException($"Le dossier {target} existe déjà et n'est pas vide.");

        var result = await RunGitAsync(parentFolder, "clone", "--", url.Trim(), name);
        EnsureSuccess(result, "cloner le dépôt");

        return target;
    }

    // ========== Outils internes ==========

    // "joyce...origin/joyce [ahead 1]" → "joyce"
    private static string ParseBranch(string header)
    {
        const string noCommits = "No commits yet on ";
        if (header.StartsWith(noCommits)) return header[noCommits.Length..];

        var dots = header.IndexOf("...", StringComparison.Ordinal);
        if (dots >= 0) return header[..dots];

        var space = header.IndexOf(' ');
        return space >= 0 ? header[..space] : header;
    }

    // "https://github.com/joyce/KeycesCode.git" → "KeycesCode"
    private static string RepositoryNameFromUrl(string url)
    {
        var name = url.TrimEnd('/', '\\');
        name = name[(name.LastIndexOfAny(new[] { '/', '\\', ':' }) + 1)..];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        if (string.IsNullOrWhiteSpace(name))
            throw new GitException("L'URL du dépôt n'est pas valide.");
        return name;
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

        // Pas de question dans une console invisible (la connexion GitHub passe par une fenêtre)
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

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
