using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
            var received = new ConcurrentQueue<string>();

            using var terminal = new TerminalService();
            terminal.OutputReceived += (_, e) => received.Enqueue(e.Line);

            await terminal.StartSessionAsync(tempDir);
            Assert.True(terminal.IsSessionActive);

            await terminal.SendInputAsync("echo KEYCESCODE_TERM_PING");

            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && !received.Any(line => line.Contains("KEYCESCODE_TERM_PING")))
            {
                await Task.Delay(100);
            }

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
