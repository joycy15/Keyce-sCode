using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KeycesCode.Execution.Interfaces;
using KeycesCode.Execution.Models;

namespace KeycesCode.Execution.Services
{
    public class RunService : IRunService, IDisposable
    {
        private readonly IToolDetectionService _toolDetectionService;
        private Process? _currentProcess;
        private readonly object _syncLock = new();

        public RunService(IToolDetectionService? toolDetectionService = null)
        {
            _toolDetectionService = toolDetectionService ?? new ToolDetectionService();
        }

        public bool IsRunning
        {
            get
            {
                lock (_syncLock)
                {
                    return _currentProcess is { HasExited: false };
                }
            }
        }

        public event EventHandler<TerminalOutputEventArgs>? OutputReceived;
        public event EventHandler<int>? ProcessExited;

        public async Task<ExecutionResult> RunAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(request.FilePath) || !File.Exists(request.FilePath))
                throw new FileNotFoundException("Le fichier cible à exécuter est introuvable.", request.FilePath);

            if (IsRunning)
                throw new InvalidOperationException("Une exécution est déjà en cours.");

            (string fileName, string arguments) = await BuildCommandAsync(request, isDebug: false, cancellationToken);
            string workingDir = ResolveWorkingDirectory(request.FilePath, request.WorkingDirectory);

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            var stopwatch = Stopwatch.StartNew();

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,   // accents corrects dans OUTPUT
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";   // Python écrit en UTF-8

            if (request.EnvironmentVariables != null)
            {
                foreach (var kvp in request.EnvironmentVariables)
                    startInfo.Environment[kvp.Key] = kvp.Value;
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                stdoutBuilder.AppendLine(e.Data);
                EmitOutput(e.Data, OutputStreamType.StandardOutput);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                stderrBuilder.AppendLine(e.Data);
                EmitOutput(e.Data, OutputStreamType.StandardError);
            };

            lock (_syncLock)
            {
                _currentProcess = process;
            }

            try
            {
                EmitOutput($"[Exécution] {fileName} {arguments}".Trim(), OutputStreamType.SystemMessage);

                if (!process.Start())
                    throw new InvalidOperationException("Impossible de démarrer le processus d'exécution.");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(cancellationToken);
                stopwatch.Stop();

                int exitCode = process.ExitCode;
                EmitOutput($"[Terminé] Code de sortie : {exitCode} ({stopwatch.ElapsedMilliseconds} ms)", OutputStreamType.SystemMessage);
                ProcessExited?.Invoke(this, exitCode);

                return new ExecutionResult(
                    exitCode,
                    stdoutBuilder.ToString(),
                    stderrBuilder.ToString(),
                    stopwatch.Elapsed,
                    TimedOut: false);
            }
            catch (OperationCanceledException)
            {
                await StopCurrentProcessAsync();
                stopwatch.Stop();
                EmitOutput("[Annulé] Exécution interrompue.", OutputStreamType.SystemMessage);

                return new ExecutionResult(
                    -1,
                    stdoutBuilder.ToString(),
                    stderrBuilder.ToString(),
                    stopwatch.Elapsed,
                    TimedOut: true);
            }
            catch (Exception ex)
            {
                await StopCurrentProcessAsync();
                stopwatch.Stop();
                stderrBuilder.AppendLine(ex.Message);
                EmitOutput($"[Erreur] {ex.Message}", OutputStreamType.StandardError);

                return new ExecutionResult(
                    -1,
                    stdoutBuilder.ToString(),
                    stderrBuilder.ToString(),
                    stopwatch.Elapsed,
                    TimedOut: false);
            }
            finally
            {
                lock (_syncLock)
                {
                    if (ReferenceEquals(_currentProcess, process))
                        _currentProcess = null;
                }
            }
        }

        public async Task<DebugSessionInfo> StartDebugAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(request.FilePath) || !File.Exists(request.FilePath))
                throw new FileNotFoundException("Le fichier cible à déboguer est introuvable.", request.FilePath);

            if (IsRunning)
                throw new InvalidOperationException("Une session est déjà active.");

            (string fileName, string arguments) = await BuildCommandAsync(request, isDebug: true, cancellationToken);
            string workingDir = ResolveWorkingDirectory(request.FilePath, request.WorkingDirectory);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
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
                int code = 0;
                try { code = process.ExitCode; } catch { }
                EmitOutput($"[Débogage terminé] Code de sortie : {code}", OutputStreamType.SystemMessage);
                ProcessExited?.Invoke(this, code);
                lock (_syncLock)
                {
                    if (ReferenceEquals(_currentProcess, process))
                        _currentProcess = null;
                }
                process.Dispose();
            };

            lock (_syncLock)
            {
                _currentProcess = process;
            }

            EmitOutput($"[Débogage] Démarrage sur {Path.GetFileName(request.FilePath)}...", OutputStreamType.SystemMessage);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            string debuggerName = Path.GetExtension(request.FilePath).ToLowerInvariant() switch
            {
                ".py" => "pdb",
                ".js" or ".mjs" => "node-inspect",
                ".cs" or ".csproj" or ".sln" => "dotnet-debug",
                _ => Path.GetFileNameWithoutExtension(fileName)
            };

            return new DebugSessionInfo(process.Id, request.FilePath, debuggerName, IsRunning: true);
        }

        public Task StopCurrentProcessAsync()
        {
            Process? proc;
            lock (_syncLock)
            {
                proc = _currentProcess;
            }

            if (proc != null && !proc.HasExited)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    EmitOutput("[Arrêt] Processus arrêté par l'utilisateur.", OutputStreamType.SystemMessage);
                }
                catch
                {
                    // Processus déjà terminé
                }
            }

            return Task.CompletedTask;
        }

        private static string ResolveWorkingDirectory(string filePath, string? workingDirectory)
        {
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                return Path.GetFullPath(workingDirectory);

            return Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Environment.CurrentDirectory;
        }

        private async Task<(string FileName, string Arguments)> BuildCommandAsync(
            ExecutionRequest request,
            bool isDebug,
            CancellationToken cancellationToken)
        {
            string fullPath = Path.GetFullPath(request.FilePath);
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            string extraArgs = string.IsNullOrWhiteSpace(request.Arguments) ? string.Empty : $" {request.Arguments.Trim()}";

            ToolInfo? tool = await _toolDetectionService.DetectToolForFileAsync(fullPath, cancellationToken);
            string toolExe = tool is { IsAvailable: true } ? tool.ExecutablePath : string.Empty;

            return ext switch
            {
                ".py" => (
                    string.IsNullOrEmpty(toolExe) ? "python" : toolExe,
                    isDebug ? $"-m pdb \"{fullPath}\"{extraArgs}" : $"\"{fullPath}\"{extraArgs}"
                ),
                ".js" or ".mjs" => (
                    string.IsNullOrEmpty(toolExe) ? "node" : toolExe,
                    isDebug ? $"--inspect-brk \"{fullPath}\"{extraArgs}" : $"\"{fullPath}\"{extraArgs}"
                ),
                ".csproj" or ".sln" => (
                    string.IsNullOrEmpty(toolExe) ? "dotnet" : toolExe,
                    isDebug ? $"run --configuration Debug --project \"{fullPath}\"{extraArgs}" : $"run --project \"{fullPath}\"{extraArgs}"
                ),
                ".cs" => (
                    string.IsNullOrEmpty(toolExe) ? "dotnet" : toolExe,
                    $"run \"{fullPath}\"{extraArgs}"
                ),
                _ => (fullPath, request.Arguments.Trim())
            };
        }

        private void EmitOutput(string line, OutputStreamType streamType)
        {
            OutputReceived?.Invoke(this, new TerminalOutputEventArgs(line, streamType, DateTime.Now));
        }

        public void Dispose()
        {
            StopCurrentProcessAsync().GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }
    }
}
