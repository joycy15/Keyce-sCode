using System.Diagnostics;
using KeycesCode.Settings.Git;

namespace KeycesCode.Tests.Git;

public class GitServiceTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "KeycesCodeGitTests", Guid.NewGuid().ToString());
    private readonly GitService _git = new();

    public GitServiceTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public async Task IsRepository_DossierVide_RenvoieFalse()
    {
        Assert.False(await _git.IsRepositoryAsync(_folder));
    }

    [Fact]
    public async Task IsRepository_ApresGitInit_RenvoieTrue()
    {
        InitRepo();

        Assert.True(await _git.IsRepositoryAsync(_folder));
    }

    [Fact]
    public async Task GetStatus_NouveauFichier_ApparaitCommeNonSuivi()
    {
        InitRepo();
        File.WriteAllText(Path.Combine(_folder, "a.txt"), "bonjour");

        var status = await _git.GetStatusAsync(_folder);

        Assert.False(string.IsNullOrEmpty(status.Branch));
        var file = Assert.Single(status.Files);
        Assert.Equal("a.txt", file.Path);
        Assert.Equal("??", file.Status);
    }

    [Fact]
    public async Task Commit_PuisStatus_PlusAucunFichierModifie()
    {
        InitRepo();
        File.WriteAllText(Path.Combine(_folder, "a.txt"), "bonjour");

        await _git.CommitAsync(_folder, "premier commit", new[] { "a.txt" });

        var status = await _git.GetStatusAsync(_folder);
        Assert.Empty(status.Files);
    }

    // Crée un petit dépôt Git de test dans le dossier temporaire
    private void InitRepo()
    {
        RunGit("init");
        RunGit("config", "user.name", "Test");
        RunGit("config", "user.email", "test@example.com");
    }

    private void RunGit(params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = _folder,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        using var process = Process.Start(info)!;
        process.WaitForExit();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_folder)) return;

        // Le dossier .git contient des fichiers en lecture seule : on les débloque avant de supprimer
        foreach (var file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        Directory.Delete(_folder, recursive: true);
    }
}