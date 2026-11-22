using KeyceCode.Files.Interfaces;

namespace KeyceCode.Files.Services
{
    public class WorkspaceService : IWorkspaceService
    {
        public string? WorkspaceRootPath { get; private set; }

        public void LoadWorkspace(string rootPath)
        {
            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException("Le dossier du projet est introuvable.");
            
            WorkspaceRootPath = rootPath;
        }

        public IEnumerable<string> GetAllFiles()
        {
            if (string.IsNullOrEmpty(WorkspaceRootPath) || !Directory.Exists(WorkspaceRootPath))
                return Enumerable.Empty<string>();

            return Directory.GetFiles(WorkspaceRootPath, "*.*", SearchOption.AllDirectories);
        }
    }
}