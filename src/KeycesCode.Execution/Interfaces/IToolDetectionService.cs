using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KeycesCode.Execution.Models;

namespace KeycesCode.Execution.Interfaces
{
    public interface IToolDetectionService
    {
        Task<IReadOnlyList<ToolInfo>> DetectInstalledToolsAsync(CancellationToken cancellationToken = default);
        Task<ToolInfo?> DetectToolForFileAsync(string filePath, CancellationToken cancellationToken = default);
        Task<bool> IsToolAvailableAsync(string toolIdOrCommand, CancellationToken cancellationToken = default);
    }
}
