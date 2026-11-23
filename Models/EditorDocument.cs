using ICSharpCode.AvalonEdit;
using System.Collections.Generic;
using System.IO;
using System.Threading;

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
    public bool IsLargeFile { get; set; }
    public SortedSet<int> Breakpoints { get; } = [];
    public SemaphoreSlim SaveLock { get; } = new(1, 1);
    public string DisplayName =>
        $"{(FilePath is null ? "Sans titre" : Path.GetFileName(FilePath))}" +
        $"{(Breakpoints.Count > 0 ? " ●" : string.Empty)}{(IsDirty ? " *" : string.Empty)}";
}
