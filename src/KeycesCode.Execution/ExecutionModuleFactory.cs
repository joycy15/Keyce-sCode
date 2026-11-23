using KeycesCode.Execution.Interfaces;
using KeycesCode.Execution.Services;

namespace KeycesCode.Execution
{
    public static class ExecutionModuleFactory
    {
        public static IToolDetectionService CreateToolDetectionService() => new ToolDetectionService();

        public static IRunService CreateRunService(IToolDetectionService? toolDetectionService = null) =>
            new RunService(toolDetectionService ?? CreateToolDetectionService());

        public static ITerminalService CreateTerminalService() => new TerminalService();
    }
}
