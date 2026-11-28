namespace KeycesCode.Assistant;

// Ce que l'éditeur donne à l'assistant : le fichier ouvert et le texte sélectionné
public sealed record EditorContext(string FileName, string Language, string Content, string Selection);

// Ce que l'assistant demande à l'éditeur de faire avec un bloc de code de sa réponse
public enum CodeActionKind
{
    InsertAtCursor,     // insérer à la position du curseur
    ReplaceSelection,   // remplacer le texte sélectionné
    ReplaceFile,        // remplacer tout le fichier
    NewFile             // créer un nouveau fichier dans le dossier ouvert
}

public sealed class CodeActionEventArgs(string code, CodeActionKind kind, string fileName = "") : EventArgs
{
    public string Code { get; } = code;
    public CodeActionKind Kind { get; } = kind;

    // Nom du fichier à créer (seulement pour NewFile)
    public string FileName { get; } = fileName;

    // Rempli par l'éditeur : ce qui a été fait (affiché sous la réponse) ou l'erreur
    public bool Applied { get; set; }
    public string Result { get; set; } = "";
}
