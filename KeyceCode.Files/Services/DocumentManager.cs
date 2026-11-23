using KeyceCode.Files.Interfaces;

namespace KeyceCode.Files.Services
{
    public class DocumentManager : IDocumentManager
    {
        private readonly List<string> _openDocuments = new();
        
        public string? CurrentDocumentPath { get; private set; }
        public IReadOnlyList<string> OpenDocuments => _openDocuments.AsReadOnly();

        public void OpenDocument(string filePath)
        {
            if (!_openDocuments.Contains(filePath))
            {
                _openDocuments.Add(filePath);
            }
            CurrentDocumentPath = filePath;
        }

        public void CloseDocument(string filePath)
        {
            _openDocuments.Remove(filePath);
            if (CurrentDocumentPath == filePath)
            {
                CurrentDocumentPath = _openDocuments.FirstOrDefault();
            }
        }

        public void SetActiveDocument(string filePath)
        {
            if (_openDocuments.Contains(filePath))
            {
                CurrentDocumentPath = filePath;
            }
        }
    }
}