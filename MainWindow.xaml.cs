using ICSharpCode.AvalonEdit;
using Keyce_sCode.Models;
using Keyce_sCode.Services;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Keyce_sCode;

public partial class MainWindow : Window
{
    private readonly FileService _fileService = new();
    private readonly ISyntaxHighlighter _syntaxHighlighter = new LanguageSyntaxHighlighter();
    private readonly ExecutionService _executionService = new();
    private readonly List<EditorDocument> _documents = [];
    private CancellationTokenSource? _executionCancellation;
    private string? _workspacePath;
    private bool _isDarkTheme = true;

    public MainWindow()
    {
        InitializeComponent();
        NewDocument();
        ApplyTheme();
    }

    private EditorDocument? CurrentDocument => (EditorTabs.SelectedItem as TabItem)?.Tag as EditorDocument;

    private void NewFile_Click(object sender, RoutedEventArgs e) => NewDocument();

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Ouvrir un fichier",
            Filter = "Tous les fichiers (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            OpenFile(dialog.FileName);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Ouvrir un dossier de travail" };
        if (dialog.ShowDialog(this) == true)
        {
            SetWorkspace(dialog.FolderName);
        }
    }

    private void SaveFile_Click(object sender, RoutedEventArgs e) => SaveCurrentDocument();

    private async void RunFile_Click(object sender, RoutedEventArgs e)
    {
        if (_executionCancellation is not null)
        {
            return;
        }

        var document = CurrentDocument;
        if (document is null)
        {
            return;
        }

        if (!SaveDocument(document))
        {
            return;
        }

        _executionCancellation = new CancellationTokenSource();
        var cancellation = _executionCancellation;
        RunButtonState(isRunning: true);
        ConsoleOutput.Clear();
        EditorStatus.Text = $"Exécution de {Path.GetFileName(document.FilePath)}…";
        ((TabControl)ConsoleTab.Parent).SelectedItem = ConsoleTab;

        try
        {
            await _executionService.RunAsync(
                document.FilePath!,
                output => Dispatcher.BeginInvoke(new Action(() => AppendOutput(output))),
                cancellation.Token);
            EditorStatus.Text = "Exécution terminée";
        }
        catch (OperationCanceledException)
        {
            AppendOutput(new ProcessOutput("Exécution annulée."));
            EditorStatus.Text = "Exécution annulée";
        }
        catch (Exception exception)
        {
            AppendOutput(new ProcessOutput($"Erreur : {exception.Message}", true));
            EditorStatus.Text = "Échec de l’exécution";
        }
        finally
        {
            if (ReferenceEquals(_executionCancellation, cancellation))
            {
                _executionCancellation.Dispose();
                _executionCancellation = null;
                RunButtonState(isRunning: false);
            }
        }
    }

    private void StopRun_Click(object sender, RoutedEventArgs e) => _executionCancellation?.Cancel();

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        ApplyTheme();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            switch (e.Key)
            {
                case Key.N:
                    NewDocument();
                    e.Handled = true;
                    break;
                case Key.O:
                    OpenFile_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.S:
                    SaveCurrentDocument();
                    e.Handled = true;
                    break;
            }
        }

        if (e.Key == Key.F5)
        {
            RunFile_Click(sender, e);
            e.Handled = true;
        }
    }

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == EditorTabs)
        {
            UpdateEditorStatus();
        }
    }

    private void FileTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTree.SelectedItem is not TreeViewItem item || item.Tag is not string path)
        {
            return;
        }

        if (File.Exists(path))
        {
            OpenFile(path);
        }
        else if (Directory.Exists(path))
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        foreach (var document in _documents.Where(item => item.IsDirty).ToArray())
        {
            var answer = MessageBox.Show(
                $"Enregistrer les modifications de « {document.DisplayName} » ?",
                "Fichier modifié",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                SelectDocument(document);
                if (!SaveCurrentDocument())
                {
                    e.Cancel = true;
                    return;
                }
            }
        }

        _executionCancellation?.Cancel();
    }

    private void NewDocument()
    {
        var editor = CreateEditor();
        var document = new EditorDocument(editor, null);
        editor.Document.TextChanged += (_, _) =>
        {
            document.IsDirty = true;
            UpdateDocumentTab(document);
        };

        AddDocument(document);
    }

    private void OpenFile(string path)
    {
        var existing = _documents.FirstOrDefault(document =>
            document.FilePath is not null &&
            string.Equals(Path.GetFullPath(document.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectDocument(existing);
            return;
        }

        try
        {
            var editor = CreateEditor();
            editor.Text = _fileService.ReadAllText(path);
            var document = new EditorDocument(editor, path);
            editor.Document.TextChanged += (_, _) =>
            {
                document.IsDirty = true;
                UpdateDocumentTab(document);
            };
            AddDocument(document);
        }
        catch (IOException exception)
        {
            ShowFileError(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowFileError(exception);
        }
    }

    private TextEditor CreateEditor()
    {
        var editor = new TextEditor
        {
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            WordWrap = false,
            Padding = new Thickness(8),
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
        };
        editor.SetResourceReference(Control.BackgroundProperty, "EditorBackgroundBrush");
        editor.SetResourceReference(Control.ForegroundProperty, "ForegroundBrush");
        editor.TextArea.PreviewKeyDown += (_, args) =>
        {
            if (TryExpandHtmlSnippet(editor, args.Key))
            {
                args.Handled = true;
            }
        };
        return editor;
    }

    private void AddDocument(EditorDocument document)
    {
        _documents.Add(document);
        var tab = new TabItem { Tag = document, Content = document.Editor };
        EditorTabs.Items.Add(tab);
        UpdateDocumentTab(document, refreshSyntax: true);
        SelectDocument(document);
    }

    private void SelectDocument(EditorDocument document)
    {
        var tab = EditorTabs.Items.Cast<TabItem>().First(item => ReferenceEquals(item.Tag, document));
        EditorTabs.SelectedItem = tab;
        document.Editor.Focus();
        UpdateEditorStatus();
    }

    private void UpdateDocumentTab(EditorDocument document, bool refreshSyntax = false)
    {
        var tab = EditorTabs.Items.Cast<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, document));
        if (tab is not null)
        {
            tab.Header = document.DisplayName;
        }

        if (refreshSyntax)
        {
            document.Editor.SyntaxHighlighting = _syntaxHighlighter.GetDefinition(document.FilePath, _isDarkTheme);
        }
    }

    private bool SaveCurrentDocument() =>
        CurrentDocument is not null && SaveDocument(CurrentDocument);

    private bool SaveDocument(EditorDocument document)
    {
        if (document.FilePath is not null && !document.IsDirty)
        {
            return true;
        }

        var path = document.FilePath;
        if (path is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Enregistrer le fichier",
                FileName = "Sans titre",
                Filter = "Tous les fichiers (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        try
        {
            _fileService.Save(path, document.Editor.Text);
            document.FilePath = path;
            document.IsDirty = false;
            UpdateDocumentTab(document, refreshSyntax: true);
            UpdateWorkspaceStatus();
            UpdateEditorStatus();
            return true;
        }
        catch (IOException exception)
        {
            ShowFileError(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowFileError(exception);
        }

        return false;
    }

    private void SetWorkspace(string path)
    {
        _workspacePath = Path.GetFullPath(path);
        FileTree.Items.Clear();
        var root = CreateDirectoryNode(_workspacePath, showPlaceholder: false);
        root.IsExpanded = true;
        FileTree.Items.Add(root);
        PopulateDirectory(root);
        UpdateWorkspaceStatus();
    }

    private TreeViewItem CreateDirectoryNode(string path, bool showPlaceholder)
    {
        var item = new TreeViewItem { Header = Path.GetFileName(path), Tag = path };
        item.Expanded += Directory_Expanded;
        if (showPlaceholder)
        {
            item.Items.Add(new TreeViewItem { Header = string.Empty, Tag = "placeholder" });
        }

        return item;
    }

    private void Directory_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem item && item.Items.Count == 1 &&
            (item.Items[0] as TreeViewItem)?.Tag as string == "placeholder")
        {
            PopulateDirectory(item);
        }
    }

    private void PopulateDirectory(TreeViewItem parent)
    {
        if (parent.Tag is not string directory)
        {
            return;
        }

        // Les descendants ne sont chargés qu'à l'ouverture du dossier dans l'arbre.
        parent.Items.Clear();
        try
        {
            foreach (var subdirectory in Directory.GetDirectories(directory).OrderBy(Path.GetFileName))
            {
                parent.Items.Add(CreateDirectoryNode(subdirectory, showPlaceholder: true));
            }

            foreach (var file in Directory.GetFiles(directory).OrderBy(Path.GetFileName))
            {
                parent.Items.Add(new TreeViewItem { Header = Path.GetFileName(file), Tag = file });
            }
        }
        catch (IOException exception)
        {
            ShowFileError(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowFileError(exception);
        }
    }

    private void ApplyTheme()
    {
        var colors = _isDarkTheme
            ? new Dictionary<string, Color>
            {
                ["AppBackgroundBrush"] = Color.FromRgb(30, 30, 30),
                ["PanelBackgroundBrush"] = Color.FromRgb(37, 37, 38),
                ["ToolbarBackgroundBrush"] = Color.FromRgb(51, 51, 51),
                ["EditorBackgroundBrush"] = Color.FromRgb(30, 30, 30),
                ["ForegroundBrush"] = Color.FromRgb(212, 212, 212),
                ["MutedForegroundBrush"] = Color.FromRgb(157, 160, 165),
                ["BorderBrush"] = Color.FromRgb(60, 60, 60)
            }
            : new Dictionary<string, Color>
            {
                ["AppBackgroundBrush"] = Color.FromRgb(250, 250, 250),
                ["PanelBackgroundBrush"] = Color.FromRgb(243, 243, 243),
                ["ToolbarBackgroundBrush"] = Color.FromRgb(232, 232, 232),
                ["EditorBackgroundBrush"] = Colors.White,
                ["ForegroundBrush"] = Color.FromRgb(32, 32, 32),
                ["MutedForegroundBrush"] = Color.FromRgb(95, 95, 95),
                ["BorderBrush"] = Color.FromRgb(210, 210, 210)
            };

        foreach (var (key, color) in colors)
        {
            Resources[key] = new SolidColorBrush(color);
        }

        ThemeButton.Content = _isDarkTheme ? "Thème clair" : "Thème sombre";
        foreach (var document in _documents)
        {
            UpdateDocumentTab(document, refreshSyntax: true);
        }
    }

    private void UpdateWorkspaceStatus()
    {
        WorkspaceStatus.Text = _workspacePath is null
            ? "Aucun dossier ouvert"
            : Path.GetFileName(_workspacePath);
    }

    private void UpdateEditorStatus()
    {
        var document = CurrentDocument;
        EditorStatus.Text = document is null
            ? "Prêt"
            : $"{(document.FilePath is null ? "Fichier non enregistré" : Path.GetExtension(document.FilePath))} · {document.Editor.Document.LineCount} lignes";
    }

    private void RunButtonState(bool isRunning)
    {
        StopButton.IsEnabled = isRunning;
        RunButton.IsEnabled = !isRunning;
    }

    private void AppendOutput(ProcessOutput output)
    {
        ConsoleOutput.AppendText($"{(output.IsError ? "[stderr] " : string.Empty)}{output.Text}{Environment.NewLine}");
        ConsoleOutput.ScrollToEnd();
    }

    private bool TryExpandHtmlSnippet(TextEditor editor, Key key)
    {
        if (key is not (Key.Tab or Key.Enter) ||
            !string.Equals(Path.GetExtension(CurrentDocument?.FilePath), ".html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var line = editor.Document.GetLineByOffset(editor.CaretOffset);
        var lineText = editor.Document.GetText(line);
        if (!string.Equals(lineText.Trim(), "!", StringComparison.Ordinal))
        {
            return false;
        }

        var indentLength = lineText.Length - lineText.TrimStart().Length;
        var indent = lineText[..indentLength];
        var lines = new[]
        {
            "<!DOCTYPE html>",
            "<html lang=\"fr\">",
            "<head>",
            "  <meta charset=\"UTF-8\">",
            "  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">",
            "  <title></title>",
            "</head>",
            "<body>",
            "  ",
            "</body>",
            "</html>"
        };
        var newline = Environment.NewLine;
        var snippet = string.Join(newline, lines.Select(value => indent + value));
        editor.Document.Replace(line.Offset, line.Length, snippet);
        var bodyLineOffset = snippet.IndexOf(newline + indent + "  " + newline + indent + "</body>", StringComparison.Ordinal);
        // Le curseur arrive sur la ligne vide du corps, après son indentation.
        editor.CaretOffset = line.Offset + bodyLineOffset + newline.Length + indent.Length + 2;
        return true;
    }

    private void ShowFileError(Exception exception)
    {
        MessageBox.Show(this, exception.Message, "Erreur de fichier", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
