using KeyceCode.Fakes;
using Xunit;

namespace KeyceCode.Tests
{
    public class FileServiceTests
    {
        [Fact]
        public async Task WriteAndReadFile_ShouldWorkProperly()
        {
            // 1. Arrange (Préparation)
            var fakeFileService = new FakeFileService();
            string testPath = "C:/faux/chemin/test.cs";
            string expectedContent = "Console.WriteLine('Hello Keyce Code');";

            // 2. Act (Action)
            await fakeFileService.WriteFileAsync(testPath, expectedContent);
            var actualContent = await fakeFileService.ReadFileAsync(testPath);

            // 3. Assert (Vérification)
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