using KeycesCode.Core.Models;

namespace KeycesCode.Core.Abstractions;

public interface IGitService
{
    Task<bool> IsRepositoryAsync(string folder);
    Task<GitStatus> GetStatusAsync(string folder);
    Task CommitAsync(string folder, string message, IReadOnlyList<string> files);
}