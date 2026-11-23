using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KeycesCode.Execution.Models;
using KeycesCode.Execution.Services;
using Xunit;

namespace KeycesCode.Tests
{
    public class TerminalServiceTests
    {
        [Fact]
        public async Task StartSessionAndSendInputAsync_ShouldExecuteCommandAndEmitOutput()
        {
            string tempDir = Path.GetTempPath();
            var received = new List<string>();

            using var terminal = new TerminalService();
            terminal.OutputReceived += (_, e) => received.Add(e.Line);

            await terminal.StartSessionAsync(tempDir);
            Assert.True(terminal.IsSessionActive);

            await terminal.SendInputAsync("echo KEYCESCODE_TERM_PING");
            await Task.Delay(1000);
            await terminal.StopSessionAsync();

            Assert.False(terminal.IsSessionActive);
            Assert.Contains(received, line => line.Contains("KEYCESCODE_TERM_PING"));
        }

        [Fact]
        public async Task ChangeDirectoryAsync_WithMissingFolder_ShouldThrowDirectoryNotFoundException()
        {
            using var terminal = new TerminalService();
            string invalidPath = Path.Combine(Path.GetTempPath(), $"Inexistant_{Guid.NewGuid():N}");

            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => terminal.ChangeDirectoryAsync(invalidPath));
        }
    }
}
