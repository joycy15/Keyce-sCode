using KeyceCode.Files.Interfaces;

namespace KeyceCode.Fakes
{
    public class FakeFileService : IFileService
    {
        private readonly Dictionary<string, string> _fakeFiles = new();

        public Task<string> ReadFileAsync(string filePath)
        {
            if (!_fakeFiles.ContainsKey(filePath)) 
                throw new FileNotFoundException($"Fake file not found: {filePath}");
            
            return Task.FromResult(_fakeFiles[filePath]);
        }

        public Task WriteFileAsync(string filePath, string content)
        {
            _fakeFiles[filePath] = content;
            return Task.CompletedTask;
        }

        public bool FileExists(string filePath) => _fakeFiles.ContainsKey(filePath);

        public void DeleteFile(string filePath) => _fakeFiles.Remove(filePath);
    }
}