using System;
using System.Threading;
using System.Threading.Tasks;
using KeycesCode.Execution.Models;

namespace KeycesCode.Execution.Interfaces
{
    public interface ITerminalService
    {
        bool IsSessionActive { get; }
        string CurrentWorkingDirectory { get; }

        // « powershell » ou « cmd » : pris en compte au prochain démarrage de la session
        string Shell { get; set; }
        event EventHandler<TerminalOutputEventArgs>? OutputReceived;

        Task StartSessionAsync(string workingDirectory, CancellationToken cancellationToken = default);
        Task SendInputAsync(string command, CancellationToken cancellationToken = default);
        Task ChangeDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default);
        Task StopSessionAsync();
    }
}
