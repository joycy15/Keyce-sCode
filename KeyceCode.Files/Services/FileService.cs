using KeyceCode.Files.Interfaces;

namespace KeyceCode.Files.Services
{
    public class FileService : IFileService
    {
        public async Task<string> ReadFileAsync(string filePath)
        {
            if (!FileExists(filePath)) throw new FileNotFoundException($"Le fichier {filePath} est introuvable.");
            return await File.ReadAllTextAsync(filePath);
        }

        public async Task WriteFileAsync(string filePath, string content)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            await File.WriteAllTextAsync(filePath, content);
        }

        public bool FileExists(string filePath) => File.Exists(filePath);

        public void DeleteFile(string filePath)
        {
            if (FileExists(filePath)) File.Delete(filePath);
        }
    }
}