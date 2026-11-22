using System.Linq;
using System.Threading.Tasks;
using KeycesCode.Execution.Services;
using Xunit;

namespace KeycesCode.Tests
{
    public class ToolDetectionServiceTests
    {
        [Fact]
        public async Task DetectInstalledToolsAsync_ShouldFindDotnetSdk()
        {
            var service = new ToolDetectionService();

            var tools = await service.DetectInstalledToolsAsync();
            var dotnetTool = tools.FirstOrDefault(t => t.Id == "dotnet");

            Assert.NotNull(dotnetTool);
            Assert.True(dotnetTool!.IsAvailable);
            Assert.False(string.IsNullOrWhiteSpace(dotnetTool.ExecutablePath));
            Assert.False(string.IsNullOrWhiteSpace(dotnetTool.Version));
        }

        [Fact]
        public async Task DetectToolForFileAsync_WithCSharpFile_ShouldReturnDotnet()
        {
            var service = new ToolDetectionService();

            var tool = await service.DetectToolForFileAsync("Program.cs");

            Assert.NotNull(tool);
            Assert.Equal("dotnet", tool!.Id);
            Assert.True(tool.IsAvailable);
        }

        [Fact]
        public async Task DetectToolForFileAsync_WithUnknownExtension_ShouldReturnNull()
        {
            var service = new ToolDetectionService();

            var tool = await service.DetectToolForFileAsync("notes.unknownext");

            Assert.Null(tool);
        }

        [Fact]
        public async Task IsToolAvailableAsync_WithNonExistentCommand_ShouldReturnFalse()
        {
            var service = new ToolDetectionService();

            bool available = await service.IsToolAvailableAsync("outil_inexistant_xyz_999");

            Assert.False(available);
        }
    }
}
