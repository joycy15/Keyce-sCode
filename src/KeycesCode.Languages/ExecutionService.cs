using System.Diagnostics;
using System.IO;
using System.Text;

namespace KeycesCode.Languages;

public sealed record ProcessOutput(string Text, bool IsError = false);

public interface ILanguageRunner
{
    bool Supports(string extension);
    Task<int> RunAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken);
}

public sealed class ExecutionService
{
    private readonly IReadOnlyList<ILanguageRunner> _runners =
    [
        new PythonRunner(),
        new CSharpRunner(),
        new NativeCompilerRunner(),
        new PhpRunner(),
        new WebRunner()
    ];

    public Task<int> RunAsync(string filePath, Action<ProcessOutput> report, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(filePath);
        var runner = _runners.FirstOrDefault(candidate => candidate.Supports(extension));
        if (runner is null)
        {
            throw new NotSupportedException($"L’exécution des fichiers « {extension} » n’est pas prise en charge.");
        }

        return runner.RunAsync(filePath, report, cancellationToken);
    }

    internal static async Task<int> RunProcessAsync(
        ProcessStartInfo startInfo,
        string workingDirectory,
        Action<ProcessOutput> report,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        startInfo.StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Impossible de démarrer le processus « {startInfo.FileName} ».");
        }

        using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(2));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var outputTask = ReadLinesAsync(process.StandardOutput, false, report);
        var errorTask = ReadLinesAsync(process.StandardError, true, report);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException)
        {
            // L'annulation inclut les compilateurs et autres processus enfants.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            await Task.WhenAll(outputTask, errorTask);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"Le processus « {startInfo.FileName} » a dépassé le délai de deux minutes.");
        }

        await Task.WhenAll(outputTask, errorTask);
        var exitCode = process.ExitCode;
        if (exitCode != 0)
        {
            report(new ProcessOutput($"Le processus « {startInfo.FileName} » s’est terminé avec le code {exitCode}.", true));
        }

        return exitCode;
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        bool isError,
        Action<ProcessOutput> report)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            report(new ProcessOutput(line, isError));
        }
    }
}
