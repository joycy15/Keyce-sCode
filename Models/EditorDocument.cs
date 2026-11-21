using ICSharpCode.AvalonEdit;
using System.IO;

namespace Keyce_sCode.Models;

public sealed class EditorDocument
{
    public EditorDocument(TextEditor editor, string? filePath)
    {
        Editor = editor;
        FilePath = filePath;
    }

    public TextEditor Editor { get; }
    public string? FilePath { get; set; }
    public bool IsDirty { get; set; }
    public string DisplayName => $"{(FilePath is null ? "Sans titre" : Path.GetFileName(FilePath))}{(IsDirty ? " *" : string.Empty)}";
}
