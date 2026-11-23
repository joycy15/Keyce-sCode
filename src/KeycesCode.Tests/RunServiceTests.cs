using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KeycesCode.Execution.Models;
using KeycesCode.Execution.Services;
using Xunit;

namespace KeycesCode.Tests
{
    public class RunServiceTests
    {
        [Fact]
        public async Task RunAsync_WithMissingFile_ShouldThrowFileNotFoundException()
        {
            using var runService = new RunService();
            var request = new ExecutionRequest("fichier_introuvable_12345.cs");

            await Assert.ThrowsAsync<FileNotFoundException>(() => runService.RunAsync(request));
        }

        [Fact]
        public async Task RunAsync_WithDotnetConsoleProject_ShouldCaptureStdoutAndExitZero()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"KeycesCodeTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                string csprojPath = Path.Combine(tempDir, "HelloTest.csproj");
                string programPath = Path.Combine(tempDir, "Program.cs");

                await File.WriteAllTextAsync(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>");

                await File.WriteAllTextAsync(programPath, @"System.Console.WriteLine(""KEYCESCODE_OK_42"");");

                var capturedLines = new List<TerminalOutputEventArgs>();
                using var runService = new RunService();
                runService.OutputReceived += (_, e) => capturedLines.Add(e);

                ExecutionResult result = await runService.RunAsync(new ExecutionRequest(csprojPath, tempDir));

                Assert.Equal(0, result.ExitCode);
                Assert.False(result.TimedOut);
                Assert.Contains("KEYCESCODE_OK_42", result.StandardOutput);
                Assert.Contains(capturedLines, l => l.Line.Contains("KEYCESCODE_OK_42"));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
