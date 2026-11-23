using System;
using System.Collections.Generic;

namespace KeycesCode.Execution.Models
{
    public record ToolInfo(
        string Id,
        string Name,
        string ExecutablePath,
        string? Version,
        bool IsAvailable
    );

    public record ExecutionRequest(
        string FilePath,
        string? WorkingDirectory = null,
        string Arguments = "",
        IDictionary<string, string>? EnvironmentVariables = null
    );

    public record ExecutionResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        TimeSpan Duration,
        bool TimedOut = false
    );

    public record DebugSessionInfo(
        int ProcessId,
        string TargetFile,
        string DebuggerName,
        bool IsRunning
    );

    public enum OutputStreamType
    {
        StandardOutput,
        StandardError,
        SystemMessage
    }

    public record TerminalOutputEventArgs(
        string Line,
        OutputStreamType StreamType,
        DateTime Timestamp
    );
}
