using KeycesCode.Fakes;
using Xunit;

namespace KeycesCode.Tests.Fichiers
{
    public class FileServiceTests
    {
        [Fact]
        public async Task WriteAndReadFile_ShouldWorkProperly()
        {
            // 1. Arrange (PrÃ©paration)
            var fakeFileService = new FakeFileService();
            string testPath = "C:/faux/chemin/test.cs";
            string expectedContent = "Console.WriteLine('Hello Keyce Code');";

            // 2. Act (Action)
            await fakeFileService.WriteFileAsync(testPath, expectedContent);
            var actualContent = await fakeFileService.ReadFileAsync(testPath);

            // 3. Assert (VÃ©rification)
            Assert.Equal(expectedContent, actualContent);
            Assert.True(fakeFileService.FileExists(testPath));
        }

        [Fact]
        public void DeleteFile_ShouldRemoveFile()
        {
            // Arrange
            var fakeFileService = new FakeFileService();
            string testPath = "test.txt";
            fakeFileService.WriteFileAsync(testPath, "test").Wait();

            // Act
            fakeFileService.DeleteFile(testPath);

            // Assert
            Assert.False(fakeFileService.FileExists(testPath));
        }
    }
}

