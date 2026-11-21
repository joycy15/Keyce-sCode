using KeycesCode.Core.Models;

namespace KeycesCode.Core.Abstractions;

public interface IGitService
{
    // Dépôt local
    Task<bool> IsRepositoryAsync(string folder);
    Task<GitStatus> GetStatusAsync(string folder);
    Task CommitAsync(string folder, string message, IReadOnlyList<string> files);

    // GitHub (dépôt distant)
    Task<string?> GetRemoteUrlAsync(string folder);           // null si aucune URL
    Task SetRemoteUrlAsync(string folder, string url);
    Task<string> PushAsync(string folder);                     // renvoie un message de résultat
    Task<string> PullAsync(string folder);
    Task<string> CloneAsync(string url, string parentFolder);  // renvoie le dossier créé
}
