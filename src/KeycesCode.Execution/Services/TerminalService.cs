using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KeycesCode.Execution.Interfaces;
using KeycesCode.Execution.Models;

namespace KeycesCode.Execution.Services
{
    public class TerminalService : ITerminalService, IDisposable
    {
        private Process? _shellProcess;
        private readonly object _syncLock = new();

        public bool IsSessionActive
        {
            get
            {
                lock (_syncLock)
                {
                    return _shellProcess is { HasExited: false };
                }
            }
        }

        public string CurrentWorkingDirectory { get; private set; } = Environment.CurrentDirectory;

        public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

        public Task StartSessionAsync(string workingDirectory, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsSessionActive)
                return Task.CompletedTask;

            string targetDir = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
                ? Path.GetFullPath(workingDirectory)
                : Environment.CurrentDirectory;

            CurrentWorkingDirectory = targetDir;

            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string shellExe = isWindows ? "powershell.exe" : "/bin/bash";
            string shellArgs = isWindows ? "-NoLogo -NoProfile -ExecutionPolicy Bypass" : "-i";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = shellExe,
                    Arguments = shellArgs,
                    WorkingDirectory = targetDir,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    EmitOutput(e.Data, OutputStreamType.StandardOutput);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    EmitOutput(e.Data, OutputStreamType.StandardError);
            };

            process.Exited += (_, _) =>
            {
                EmitOutput("[Terminal] Session fermée.", OutputStreamType.SystemMessage);
                lock (_syncLock)
                {
                    if (ReferenceEquals(_shellProcess, process))
                        _shellProcess = null;
                }
            };

            lock (_syncLock)
            {
                _shellProcess = process;
            }

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            EmitOutput($"[Terminal] Session démarrée dans {CurrentWorkingDirectory}", OutputStreamType.SystemMessage);
            return Task.CompletedTask;
        }

        public async Task SendInputAsync(string command, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(command))
                return;

            if (!IsSessionActive)
                await StartSessionAsync(CurrentWorkingDirectory, cancellationToken);

            Process? proc;
            lock (_syncLock)
            {
                proc = _shellProcess;
            }

            if (proc == null || proc.HasExited)
                throw new InvalidOperationException("La session terminal n'est pas active.");

            await proc.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
            await proc.StandardInput.FlushAsync(cancellationToken);
        }

        public async Task ChangeDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
                throw new ArgumentException("Le chemin du répertoire est requis.", nameof(directoryPath));

            string fullPath = Path.GetFullPath(directoryPath);
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"Répertoire introuvable : {fullPath}");

            CurrentWorkingDirectory = fullPath;

            if (IsSessionActive)
                await SendInputAsync($"cd \"{fullPath}\"", cancellationToken);
        }

        public Task StopSessionAsync()
        {
            Process? proc;
            lock (_syncLock)
            {
                proc = _shellProcess;
                _shellProcess = null;
            }

            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignorer si déjà fermé
                }
                finally
                {
                    proc.Dispose();
                }
            }

            return Task.CompletedTask;
        }

        private void EmitOutput(string line, OutputStreamType streamType)
        {
            OutputReceived?.Invoke(this, new TerminalOutputEventArgs(line, streamType, DateTime.Now));
        }

        public void Dispose()
        {
            StopSessionAsync().GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }
    }
}
