namespace KeyceCode.Files.Interfaces
{
    public interface IWorkspaceService
    {
        string? WorkspaceRootPath { get; }
        void LoadWorkspace(string rootPath);
        IEnumerable<string> GetAllFiles();
    }
}