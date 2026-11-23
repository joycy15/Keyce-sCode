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
    }
}
