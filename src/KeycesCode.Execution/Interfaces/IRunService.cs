using System;
using System.Threading;
using System.Threading.Tasks;
using KeycesCode.Execution.Models;

namespace KeycesCode.Execution.Interfaces
{
    public interface IRunService
    {
        bool IsRunning { get; }
        event EventHandler<TerminalOutputEventArgs>? OutputReceived;
        event EventHandler<int>? ProcessExited;

        Task<ExecutionResult> RunAsync(ExecutionRequest request, CancellationToken cancellationToken = default);
        Task<DebugSessionInfo> StartDebugAsync(ExecutionRequest request, CancellationToken cancellationToken = default);
        Task StopCurrentProcessAsync();

        // Envoie une ligne au programme en cours (ce qu'il lit avec Console.ReadLine / input())
        Task SendInputAsync(string text, CancellationToken cancellationToken = default);
    }
}
