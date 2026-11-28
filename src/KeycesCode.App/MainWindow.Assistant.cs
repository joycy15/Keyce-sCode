using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using KeycesCode.Assistant;

namespace KeycesCode.App;

// Lien entre l'assistant IA et l'éditeur :
//  - l'assistant reçoit le fichier ouvert et la sélection avec chaque question
//  - il écrit lui-même son code dans l'éditeur (remplacer le fichier, la sélection, insérer, créer un fichier)
//  - clic droit dans l'éditeur : Expliquer, Corriger, Commenter, Améliorer avec l'assistant
public partial class MainWindow
{
    private void InitializeAssistantLink()
    {
        AssistantView.ContextProvider = GetEditorContext;
        AssistantView.WorkspaceProvider = () => HasWorkspace ? _workspacePath : null;
        AssistantView.CodeActionRequested += AssistantView_CodeActionRequested;
        AssistantView.CloseRequested += (_, _) => SetChatVisible(false);
        EditorTabs.SelectionChanged += (_, _) => AssistantView.RefreshContext();
    }

    private TextEditor? CurrentEditor =>
        EditorTabs.SelectedItem is TabItem { Tag: string path } &&
        _documents.TryGetValue(path, out EditorDocument? document)
            ? document.Editor
            : null;

    // Le fichier affiché dans l'éditeur, donné à l'assistant
    private EditorContext? GetEditorContext()
    {
        if (EditorTabs.SelectedItem is not TabItem { Tag: string path } ||
            !_documents.TryGetValue(path, out EditorDocument? document))
        {
            return null;
        }

        return new EditorContext(
            Path.GetFileName(path),
            LanguageName(path),
            document.Editor.Text,
            document.Editor.SelectedText);
    }

    private static string LanguageName(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".py" => "python",
        ".js" or ".mjs" => "javascript",
        ".ts" => "typescript",
        ".html" or ".htm" => "html",
        ".css" => "css",
        ".php" => "php",
        ".java" => "java",
        ".c" or ".h" => "c",
        ".cpp" or ".cc" or ".hpp" => "cpp",
        ".sql" => "sql",
        ".json" => "json",
        ".xml" or ".xaml" or ".csproj" => "xml",
        ".md" => "markdown",
        _ => "text"
    };

    // L'assistant écrit dans l'éditeur : automatiquement (mode « écrire directement ») ou après un clic sur un bouton
    private void AssistantView_CodeActionRequested(object? sender, CodeActionEventArgs e)
    {
        if (e.Kind == CodeActionKind.NewFile)
        {
            WriteNewFileFromAssistant(e);
            return;
        }

        TextEditor? editor = CurrentEditor;
        if (editor is null)
        {
            e.Result = "Aucun fichier ouvert : ouvre un fichier, ou demande à l'assistant de créer un nouveau fichier.";
            StatusText.Text = e.Result;
            return;
        }

        string fileName = EditorTabs.SelectedItem is TabItem { Tag: string path } ? Path.GetFileName(path) : "le fichier";
        var document = editor.Document;
        int caret;

        switch (e.Kind)
        {
            case CodeActionKind.ReplaceFile:
                // Replace (et non Text = ...) : Ctrl+Z permet de revenir en arrière
                document.Replace(0, document.TextLength, e.Code);
                caret = Math.Min(editor.CaretOffset, document.TextLength);
                e.Result = $"Écrit dans {fileName} : tout le fichier a été remplacé (Ctrl+Z pour annuler, Ctrl+S pour enregistrer).";
                break;

            case CodeActionKind.ReplaceSelection when editor.SelectionLength > 0:
                int start = editor.SelectionStart;
                document.Replace(start, editor.SelectionLength, e.Code);
                caret = start + e.Code.Length;
                e.Result = $"Écrit dans {fileName} : la sélection a été remplacée (Ctrl+Z pour annuler).";
                break;

            default:
                int offset = editor.CaretOffset;
                document.Insert(offset, e.Code);
                caret = offset + e.Code.Length;
                e.Result = $"Écrit dans {fileName} : code inséré au curseur (Ctrl+Z pour annuler).";
                break;
        }

        editor.CaretOffset = caret;
        editor.TextArea.Caret.BringCaretToView();
        e.Applied = true;
        StatusText.Text = e.Result;
    }

    // ```python:new=jeu.py → crée (ou remplace) le fichier dans le dossier ouvert, puis l'ouvre
    private void WriteNewFileFromAssistant(CodeActionEventArgs e)
    {
        if (!HasWorkspace)
        {
            e.Result = $"Impossible de créer {e.FileName} : ouvre d'abord un dossier (Fichier → Ouvrir un dossier).";
            StatusText.Text = e.Result;
            return;
        }

        string root = Path.GetFullPath(_workspacePath);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(root, e.FileName.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            e.Result = $"Nom de fichier invalide : {e.FileName}";
            return;
        }

        // Sécurité : l'assistant ne peut écrire que dans le dossier ouvert
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            e.Result = $"{e.FileName} est en dehors du dossier ouvert : fichier non créé.";
            return;
        }

        string relative = Path.GetRelativePath(root, fullPath);

        // Déjà ouvert dans un onglet : on remplace son contenu (Ctrl+Z possible)
        if (_documents.TryGetValue(fullPath, out EditorDocument? opened))
        {
            opened.Editor.Document.Replace(0, opened.Editor.Document.TextLength, e.Code);
            EditorTabs.SelectedItem = opened.Tab;
            e.Applied = true;
            e.Result = $"Écrit dans {relative} : tout le fichier a été remplacé (Ctrl+S pour enregistrer).";
            StatusText.Text = e.Result;
            return;
        }

        try
        {
            bool existed = File.Exists(fullPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, e.Code);
            if (!existed) LoadExplorer();   // nouveau fichier : il apparaît dans l'explorateur
            OpenDocument(fullPath);
            e.Applied = true;
            e.Result = existed ? $"Écrit dans {relative} (fichier du projet, maintenant ouvert)." : $"Fichier {relative} créé et ouvert.";
            StatusText.Text = e.Result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            e.Result = $"Impossible d'écrire {relative} : {ex.Message}";
            StatusText.Text = e.Result;
        }
    }

    // Menu du clic droit dans l'éditeur
    private ContextMenu CreateEditorMenu(TextEditor editor)
    {
        var menu = new ContextMenu();

        MenuItem MakeCommand(string header, ICommand command, string gesture) =>
            new() { Header = header, Command = command, CommandTarget = editor.TextArea, InputGestureText = gesture };

        menu.Items.Add(MakeCommand("Annuler", ApplicationCommands.Undo, "Ctrl+Z"));
        menu.Items.Add(MakeCommand("Rétablir", ApplicationCommands.Redo, "Ctrl+Y"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeCommand("Couper", ApplicationCommands.Cut, "Ctrl+X"));
        menu.Items.Add(MakeCommand("Copier", ApplicationCommands.Copy, "Ctrl+C"));
        menu.Items.Add(MakeCommand("Coller", ApplicationCommands.Paste, "Ctrl+V"));
        menu.Items.Add(MakeCommand("Tout sélectionner", ApplicationCommands.SelectAll, "Ctrl+A"));
        menu.Items.Add(new Separator());

        menu.Items.Add(AskItem("Assistant : expliquer ce code",
            "Explique ce code simplement, étape par étape."));
        menu.Items.Add(AskItem("Assistant : corriger les erreurs",
            "Trouve et corrige les erreurs de ce code. Donne le code corrigé complet."));
        menu.Items.Add(AskItem("Assistant : ajouter des commentaires",
            "Ajoute des commentaires clairs en français à ce code. Donne le code complet commenté."));
        menu.Items.Add(AskItem("Assistant : améliorer le code",
            "Améliore ce code (lisibilité, bonnes pratiques) sans changer ce qu'il fait. Donne le code complet."));
        return menu;
    }

    private MenuItem AskItem(string header, string question)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            ShowPanel("Assistant");
            AssistantView.RefreshContext();
            await AssistantView.AskAsync(question);
        };
        return item;
    }
}
