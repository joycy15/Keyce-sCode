using System;
using System.Threading;
using System.Threading.Tasks;
using KeycesCode.Execution.Models;

namespace KeycesCode.Execution
{
    public static class ExecutionSampleUsage
    {
        public static async Task RunQuickDiagnosticAsync(string workingDirectory, CancellationToken cancellationToken = default)
        {
            var detector = ExecutionModuleFactory.CreateToolDetectionService();
            var tools = await detector.DetectInstalledToolsAsync(cancellationToken);

            foreach (var tool in tools)
            {
                Console.WriteLine($"{tool.Name} ({tool.Id}) => Disponible: {tool.IsAvailable} | Version: {tool.Version ?? "N/A"}");
            }

            using var terminal = (IDisposable)ExecutionModuleFactory.CreateTerminalService();
            var termService = (Interfaces.ITerminalService)terminal;
            termService.OutputReceived += (_, e) => Console.WriteLine($"[{e.StreamType}] {e.Line}");

            await termService.StartSessionAsync(workingDirectory, cancellationToken);
            await termService.SendInputAsync("dotnet --info", cancellationToken);
            await Task.Delay(1200, cancellationToken);
            await termService.StopSessionAsync();
        }
    }
}
