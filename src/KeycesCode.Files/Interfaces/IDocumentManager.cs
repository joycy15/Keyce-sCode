namespace KeycesCode.Files.Interfaces
{
    public interface IDocumentManager
    {
        string? CurrentDocumentPath { get; }
        IReadOnlyList<string> OpenDocuments { get; }
        
        void OpenDocument(string filePath);
        void CloseDocument(string filePath);
        void SetActiveDocument(string filePath);
    }
}
