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
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";       // input("...") s'affiche tout de suite
            // .NET : pas de message de bienvenue ni de télémétrie (démarrage plus rapide)
            startInfo.Environment["DOTNET_NOLOGO"] = "1";
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

            if (request.EnvironmentVariables != null)
            {
                foreach (var kvp in request.EnvironmentVariables)
                    startInfo.Environment[kvp.Key] = kvp.Value;
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            lock (_syncLock)
            {
                _currentProcess = process;
            }

            try
            {
                EmitOutput($"[Exécution] {fileName} {arguments}".Trim(), OutputStreamType.SystemMessage);

                if (!process.Start())
                    throw new InvalidOperationException("Impossible de démarrer le processus d'exécution.");

                // Lecture caractère par caractère (et non ligne par ligne) : une question écrite
                // avec Console.Write("Nombre : ") s'affiche tout de suite, avant la réponse.
                Task stdoutPump = PumpAsync(process.StandardOutput, OutputStreamType.StandardOutput, stdoutBuilder);
                Task stderrPump = PumpAsync(process.StandardError, OutputStreamType.StandardError, stderrBuilder);

                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(stdoutPump, stderrPump);
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
                    RedirectStandardInput = true,   // pour taper les commandes du débogueur
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

        public async Task SendInputAsync(string text, CancellationToken cancellationToken = default)
        {
            Process? proc;
            lock (_syncLock)
            {
                proc = _currentProcess;
            }

            if (proc == null || proc.HasExited)
                throw new InvalidOperationException("Aucun programme n'est en cours d'exécution.");

            await proc.StandardInput.WriteLineAsync(text.AsMemory(), cancellationToken);
            await proc.StandardInput.FlushAsync(cancellationToken);
        }

        // Lit un flux de sortie au fil de l'eau. Une ligne complète est envoyée dès son retour à la ligne ;
        // un début de ligne qui attend (une question sans retour à la ligne) est envoyé après 150 ms de silence.
        private Task PumpAsync(StreamReader reader, OutputStreamType type, StringBuilder all)
        {
            return Task.Run(async () =>
            {
                var buffer = new char[1024];
                var line = new StringBuilder();

                while (true)
                {
                    Task<int> read = reader.ReadAsync(buffer, 0, buffer.Length);

                    if (line.Length > 0 && await Task.WhenAny(read, Task.Delay(150)) != read)
                    {
                        all.Append(line);
                        EmitOutput(line.ToString(), type);
                        line.Clear();
                    }

                    int count = await read;
                    if (count == 0) break;

                    for (int i = 0; i < count; i++)
                    {
                        char c = buffer[i];
                        if (c == '\n')
                        {
                            if (line.Length > 0 && line[^1] == '\r') line.Length--;
                            all.AppendLine(line.ToString());
                            EmitOutput(line.ToString(), type);
                            line.Clear();
                        }
                        else
                        {
                            line.Append(c);
                        }
                    }
                }

                if (line.Length > 0)
                {
                    all.Append(line);
                    EmitOutput(line.ToString(), type);
                }
            });
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
                // Fichier .cs dans un dossier qui a son projet (.csproj) : on lance le projet ;
                // sinon « dotnet run fichier.cs » (.NET 10). Le 2e lancement sans modification est bien plus rapide.
                ".cs" when FindProject(fullPath) is { } project => (
                    string.IsNullOrEmpty(toolExe) ? "dotnet" : toolExe,
                    $"run --project \"{project}\"{extraArgs}"
                ),
                ".cs" => (
                    string.IsNullOrEmpty(toolExe) ? "dotnet" : toolExe,
                    $"run \"{fullPath}\"{extraArgs}"
                ),
                _ => (fullPath, request.Arguments.Trim())
            };
        }

        private static string? FindProject(string csFile)
        {
            string? folder = Path.GetDirectoryName(csFile);
            if (folder is null) return null;
            string[] projects = Directory.GetFiles(folder, "*.csproj");
            return projects.Length == 1 ? projects[0] : null;
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
