using System.Diagnostics;
using KeycesCode.Settings.Git;

namespace KeycesCode.Tests.Git;

public class GitHubTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "KeycesCodeGitHubTests", Guid.NewGuid().ToString());
    private readonly GitService _git = new();

    private string Local => Path.Combine(_root, "local");                 // le dépôt sur le PC
    private string Remote => Path.Combine(_root, "depot-distant.git");    // le faux GitHub

    public GitHubTests()
    {
        Directory.CreateDirectory(Local);
        Directory.CreateDirectory(Remote);

        RunGit(Local, "init", "-b", "main");
        RunGit(Local, "config", "user.name", "Test");
        RunGit(Local, "config", "user.email", "test@example.com");
        RunGit(Remote, "init", "--bare", "-b", "main");
    }

    [Fact]
    public async Task GetRemoteUrl_SansRemote_RenvoieNull()
    {
        Assert.Null(await _git.GetRemoteUrlAsync(Local));
    }

    [Fact]
    public async Task SetRemoteUrl_PuisGet_RenvoieLaMemeUrl()
    {
        await _git.SetRemoteUrlAsync(Local, Remote);

        Assert.Equal(Remote, await _git.GetRemoteUrlAsync(Local));
    }

    [Fact]
    public async Task SetRemoteUrl_UrlVide_LeveUneErreur()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _git.SetRemoteUrlAsync(Local, "   "));
    }

    [Fact]
    public async Task Push_PuisClone_RetrouveLeFichier()
    {
        File.WriteAllText(Path.Combine(Local, "a.txt"), "bonjour");
        await _git.CommitAsync(Local, "premier commit", new[] { "a.txt" });
        await _git.SetRemoteUrlAsync(Local, Remote);

        await _git.PushAsync(Local);
        var clone = await _git.CloneAsync(Remote, _root);   // crée _root\depot-distant

        Assert.True(File.Exists(Path.Combine(clone, "a.txt")));
    }

    [Fact]
    public async Task Pull_RecupereLesNouveauxCommits()
    {
        File.WriteAllText(Path.Combine(Local, "a.txt"), "v1");
        await _git.CommitAsync(Local, "v1", new[] { "a.txt" });
        await _git.SetRemoteUrlAsync(Local, Remote);
        await _git.PushAsync(Local);
        var clone = await _git.CloneAsync(Remote, _root);

        // Un nouveau commit est envoyé depuis le dépôt local…
        File.WriteAllText(Path.Combine(Local, "b.txt"), "nouveau");
        await _git.CommitAsync(Local, "v2", new[] { "b.txt" });
        await _git.PushAsync(Local);

        // … et le clone le récupère avec pull
        await _git.PullAsync(clone);

        Assert.True(File.Exists(Path.Combine(clone, "b.txt")));
    }

    private static void RunGit(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        using var process = Process.Start(info)!;
        process.WaitForExit();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;

        foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        Directory.Delete(_root, recursive: true);
    }
}
