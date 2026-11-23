namespace KeycesCode.Files.Interfaces
{
    public interface IFileService
    {
        Task<string> ReadFileAsync(string filePath);
        Task WriteFileAsync(string filePath, string content);
        bool FileExists(string filePath);
        void DeleteFile(string filePath);
    }
}
