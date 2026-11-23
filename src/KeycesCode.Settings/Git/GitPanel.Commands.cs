using KeycesCode.Core.Models;

namespace KeycesCode.Settings.Git;

// Deuxième morceau de la classe GitPanel : les actions appelables depuis l'extérieur (raccourcis)
public partial class GitPanel
{
    public Task RefreshCommandAsync() => RefreshAsync();

    public Task PullCommandAsync() =>
        RunAsync("Pull en cours…", () => _git.PullAsync(_folder!));

    public Task PushCommandAsync() =>
        RunAsync("Push en cours…", async () =>
        {
            if (await _git.GetRemoteUrlAsync(_folder!) is null)
                throw new GitException("Ajoute d'abord l'URL du dépôt GitHub.");
            return await _git.PushAsync(_folder!);
        });
}
