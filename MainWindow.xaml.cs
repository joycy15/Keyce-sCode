using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Keyce_sCode.Models;
using Keyce_sCode.Services;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Keyce_sCode;

public partial class MainWindow : Window
{
    private const int MaximumHighlightedDocumentLength = 1_000_000;
    private readonly FileService _fileService = new();
    private readonly ISyntaxHighlighter _syntaxHighlighter = new LanguageSyntaxHighlighter();
    private readonly ExecutionService _executionService = new();
    private readonly List<EditorDocument> _documents = [];
    private readonly HashSet<string> _openingFiles = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _executionCancellation;
    private CancellationTokenSource? _debugCancellation;
    private DebugSession? _debugSession;
    private int? _debugThreadId;
    private JsonElement? _debugPendingStoppedEvent;
    private bool _debugPendingTermination;
    private string? _workspacePath;
    private bool _isDarkTheme = true;
    private bool _isClosing;
    private bool _allowClose;

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

    private async void SaveFile_Click(object sender, RoutedEventArgs e) =>
        await SaveCurrentDocumentAsync();

    private async void RunFile_Click(object sender, RoutedEventArgs e)
    {
        if (_executionCancellation is not null || _debugSession is not null || _debugCancellation is not null)
        {
            return;
        }

        var document = CurrentDocument;
        if (document is null)
        {
            return;
        }

        _executionCancellation = new CancellationTokenSource();
        var cancellation = _executionCancellation;
        RunButtonState(isRunning: true);

        try
        {
            if (!await SaveDocumentAsync(document, cancellation.Token))
            {
                return;
            }

            cancellation.Token.ThrowIfCancellationRequested();
            ConsoleOutput.Clear();
            EditorStatus.Text = $"Exécution de {Path.GetFileName(document.FilePath)}…";
            ((TabControl)ConsoleTab.Parent).SelectedItem = ConsoleTab;
            var exitCode = await _executionService.RunAsync(
                document.FilePath!,
                output => Dispatcher.BeginInvoke(new Action(() => AppendOutput(output))),
                cancellation.Token);
            EditorStatus.Text = exitCode == 0
                ? "Exécution terminée"
                : $"Échec (code de sortie {exitCode})";
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

    private async void DebugFile_Click(object sender, RoutedEventArgs e)
    {
        if (_debugSession is not null)
        {
            await SendDebugRequestAsync("continue", new { threadId = _debugThreadId });
            return;
        }

        if (_debugCancellation is not null || _executionCancellation is not null)
        {
            return;
        }

        var document = CurrentDocument;
        if (document is null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _debugCancellation = cancellation;
        UpdateDebugControls(isActive: true, isPaused: false);

        try
        {
            if (!await SaveAllDirtyDocumentsAsync(cancellation.Token) || document.FilePath is null)
            {
                return;
            }

            cancellation.Token.ThrowIfCancellationRequested();
            ConsoleOutput.Clear();
            EditorStatus.Text = $"Démarrage du débogueur pour {Path.GetFileName(document.FilePath)}…";
            ((TabControl)ConsoleTab.Parent).SelectedItem = ConsoleTab;
            var breakpoints = _documents
                .Where(item => item.FilePath is not null && item.Breakpoints.Count > 0)
                .ToDictionary(
                    item => Path.GetFullPath(item.FilePath!),
                    item => (IReadOnlyCollection<int>)item.Breakpoints.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            _debugSession = await DebugSession.StartAsync(
                document.FilePath,
                breakpoints,
                output => Dispatcher.BeginInvoke(new Action(() => AppendOutput(output))),
                message => Dispatcher.BeginInvoke(new Action(() => HandleDebugEvent(message))),
                cancellation.Token);

            EditorStatus.Text = "Débogueur connecté";
            UpdateDebugControls(isActive: true, isPaused: false);
            if (_debugPendingTermination)
            {
                _debugPendingTermination = false;
                _ = StopDebugSessionAsync();
            }
            else if (_debugPendingStoppedEvent is { } stoppedEvent)
            {
                _debugPendingStoppedEvent = null;
                HandleDebugEvent(stoppedEvent);
            }
        }
        catch (OperationCanceledException)
        {
            EditorStatus.Text = "Démarrage du débogueur annulé";
        }
        catch (Exception exception)
        {
            AppendOutput(new ProcessOutput($"Erreur de débogage : {exception.Message}", true));
            EditorStatus.Text = "Échec du débogage";
        }
        finally
        {
            if (ReferenceEquals(_debugCancellation, cancellation))
            {
                cancellation.Dispose();
                _debugCancellation = null;
                if (_debugSession is null)
                {
                    UpdateDebugControls(isActive: false, isPaused: false);
                }
            }
        }
    }

    private void ToggleBreakpoint()
    {
        var document = CurrentDocument;
        if (document?.FilePath is null)
        {
            return;
        }

        var line = document.Editor.Document.GetLineByOffset(document.Editor.CaretOffset).LineNumber;
        if (document.Breakpoints.Remove(line))
        {
            EditorStatus.Text = $"Point d’arrêt supprimé, ligne {line}";
        }
        else
        {
            document.Breakpoints.Add(line);
            EditorStatus.Text = $"Point d’arrêt ajouté, ligne {line}";
        }

        UpdateDocumentTab(document);
        if (_debugSession is not null)
        {
            _ = UpdateDocumentBreakpointsAsync(document);
        }
    }

    private void StepOver_Click(object sender, RoutedEventArgs e) =>
        _ = SendDebugRequestAsync("next", new { threadId = _debugThreadId });

    private void StepIn_Click(object sender, RoutedEventArgs e) =>
        _ = SendDebugRequestAsync("stepIn", new { threadId = _debugThreadId });

    private void StepOut_Click(object sender, RoutedEventArgs e) =>
        _ = SendDebugRequestAsync("stepOut", new { threadId = _debugThreadId });

    private async void StopDebug_Click(object sender, RoutedEventArgs e)
    {
        _debugCancellation?.Cancel();
        await StopDebugSessionAsync();
    }

    private async Task<bool> SaveAllDirtyDocumentsAsync(CancellationToken cancellationToken)
    {
        foreach (var document in _documents.Where(item => item.IsDirty).ToArray())
        {
            SelectDocument(document);
            if (!await SaveDocumentAsync(document, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private async Task SendDebugRequestAsync(string command, object arguments)
    {
        if (_debugSession is null || _debugThreadId is null)
        {
            return;
        }

        _debugThreadId = null;
        try
        {
            await _debugSession.RequestAsync(command, arguments, CancellationToken.None);
            UpdateDebugControls(isActive: true, isPaused: false);
        }
        catch (Exception exception)
        {
            AppendOutput(new ProcessOutput($"Commande de débogage « {command} » : {exception.Message}", true));
        }
    }

    private async Task UpdateDocumentBreakpointsAsync(EditorDocument document)
    {
        if (_debugSession is null || document.FilePath is null)
        {
            return;
        }

        try
        {
            await _debugSession.RequestAsync("setBreakpoints", new
            {
                source = new { name = Path.GetFileName(document.FilePath), path = Path.GetFullPath(document.FilePath) },
                breakpoints = document.Breakpoints.Order().Select(line => new { line }).ToArray(),
                sourceModified = false
            }, CancellationToken.None);
        }
        catch (Exception exception)
        {
            AppendOutput(new ProcessOutput($"Mise à jour des points d’arrêt : {exception.Message}", true));
        }
    }

    private void HandleDebugEvent(JsonElement message)
    {
        if (!message.TryGetProperty("event", out var eventNameElement))
        {
            return;
        }

        var eventName = eventNameElement.GetString();
        var body = message.TryGetProperty("body", out var bodyElement) ? bodyElement : default;
        switch (eventName)
        {
            case "output":
                var text = body.TryGetProperty("output", out var outputElement) ? outputElement.GetString() : null;
                if (!string.IsNullOrEmpty(text))
                {
                    var isError = body.TryGetProperty("category", out var category) &&
                                  category.GetString() is "stderr" or "telemetry";
                    AppendOutput(new ProcessOutput(text.TrimEnd(), isError));
                }

                break;
            case "stopped":
                if (_debugSession is null)
                {
                    _debugPendingStoppedEvent = message.Clone();
                    return;
                }

                _debugThreadId = body.TryGetProperty("threadId", out var threadId) ? threadId.GetInt32() : null;
                UpdateDebugControls(isActive: true, isPaused: true);
                _ = InspectStoppedThreadAsync(_debugThreadId ?? 1);
                break;
            case "continued":
                UpdateDebugControls(isActive: true, isPaused: false);
                break;
            case "breakpoint":
                if (body.TryGetProperty("reason", out var breakpointReason) &&
                    breakpointReason.GetString() == "changed" &&
                    body.TryGetProperty("breakpoint", out var changedBreakpoint) &&
                    changedBreakpoint.TryGetProperty("line", out var breakpointLine))
                {
                    AppendOutput(new ProcessOutput($"Point d’arrêt confirmé à la ligne {breakpointLine.GetInt32()}."));
                }

                break;
            case "terminated":
                if (_debugSession is null)
                {
                    _debugPendingTermination = true;
                    return;
                }

                _ = StopDebugSessionAsync();
                break;
            case "exited":
                if (body.TryGetProperty("exitCode", out var exitCode))
                {
                    AppendOutput(new ProcessOutput($"Programme terminé (code {exitCode.GetInt32()})."));
                }

                break;
        }
    }

    private async Task InspectStoppedThreadAsync(int threadId)
    {
        if (_debugSession is null)
        {
            return;
        }

        try
        {
            var stackResponse = await _debugSession.RequestAsync(
                "stackTrace", new { threadId, levels = 10 }, CancellationToken.None);
            if (!stackResponse.TryGetProperty("body", out var stackBody) ||
                !stackBody.TryGetProperty("stackFrames", out var frames) ||
                frames.GetArrayLength() == 0)
            {
                AppendOutput(new ProcessOutput("Le débogueur n’a pas fourni de pile d’appels."));
                return;
            }

            var topFrame = frames[0];
            var frameId = topFrame.GetProperty("id").GetInt32();
            var name = topFrame.TryGetProperty("name", out var frameName) ? frameName.GetString() : "(fonction)";
            var line = topFrame.TryGetProperty("line", out var frameLine) ? frameLine.GetInt32() : 0;
            var sourceName = topFrame.TryGetProperty("source", out var source) &&
                             source.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            AppendOutput(new ProcessOutput($"Arrêt dans {name} — {sourceName ?? "source inconnue"}:{line}"));

            var scopesResponse = await _debugSession.RequestAsync(
                "scopes", new { frameId }, CancellationToken.None);
            if (!scopesResponse.TryGetProperty("body", out var scopesBody) ||
                !scopesBody.TryGetProperty("scopes", out var scopes))
            {
                return;
            }

            foreach (var scope in scopes.EnumerateArray().Where(scope =>
                         scope.TryGetProperty("expensive", out var expensive) && !expensive.GetBoolean() ||
                         scope.TryGetProperty("name", out var scopeName) &&
                         scopeName.GetString()?.Contains("local", StringComparison.OrdinalIgnoreCase) == true))
            {
                if (!scope.TryGetProperty("variablesReference", out var reference) || reference.GetInt32() == 0)
                {
                    continue;
                }

                var variablesResponse = await _debugSession.RequestAsync(
                    "variables", new { variablesReference = reference.GetInt32() }, CancellationToken.None);
                if (variablesResponse.TryGetProperty("body", out var variablesBody) &&
                    variablesBody.TryGetProperty("variables", out var variables))
                {
                    foreach (var variable in variables.EnumerateArray().Take(50))
                    {
                        var variableName = variable.TryGetProperty("name", out var variableNameElement)
                            ? variableNameElement.GetString()
                            : "?";
                        var value = variable.TryGetProperty("value", out var valueElement)
                            ? valueElement.GetString()
                            : string.Empty;
                        AppendOutput(new ProcessOutput($"  {variableName} = {value}"));
                    }
                }
            }
        }
        catch (Exception exception)
        {
            AppendOutput(new ProcessOutput($"Inspection du débogueur : {exception.Message}", true));
        }
    }

    private async Task StopDebugSessionAsync()
    {
        var session = _debugSession;
        if (session is null)
        {
            UpdateDebugControls(isActive: false, isPaused: false);
            return;
        }

        _debugSession = null;
        _debugThreadId = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StopAsync(timeout.Token);
        UpdateDebugControls(isActive: false, isPaused: false);
        EditorStatus.Text = "Débogage terminé";
    }

    private void UpdateDebugControls(bool isActive, bool isPaused)
    {
        DebugButton.Content = isActive && isPaused ? "Continuer" : "Déboguer";
        DebugButton.IsEnabled = (isActive && isPaused) ||
                                (!isActive && _executionCancellation is null && _debugCancellation is null);
        StepInButton.IsEnabled = isActive && isPaused;
        StepOverButton.IsEnabled = isActive && isPaused;
        StepOutButton.IsEnabled = isActive && isPaused;
        StopDebugButton.IsEnabled = isActive || _debugCancellation is not null;
        RunButton.IsEnabled = !isActive && _executionCancellation is null && _debugCancellation is null;
    }

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
                    _ = SaveCurrentDocumentAsync();
                    e.Handled = true;
                    break;
                case Key.F5:
                    RunFile_Click(sender, e);
                    e.Handled = true;
                    break;
            }
        }

        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.F5 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            StopDebug_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            DebugFile_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F9)
        {
            ToggleBreakpoint();
            e.Handled = true;
        }
        else if (e.Key == Key.F10)
        {
            StepOver_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F11 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            StepOut_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            StepIn_Click(sender, e);
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

    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem item && item.Tag is string path && File.Exists(path))
        {
            OpenFile(path);
        }
    }

    private void FileTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTree.SelectedItem is not TreeViewItem item || item.Tag is not string path)
        {
            return;
        }

        if (Directory.Exists(path))
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private void NewWorkspaceFile_Click(object sender, RoutedEventArgs e) => CreateWorkspaceEntry(isDirectory: false);

    private void NewWorkspaceFolder_Click(object sender, RoutedEventArgs e) => CreateWorkspaceEntry(isDirectory: true);

    private void RefreshWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_workspacePath is not null)
        {
            SetWorkspace(_workspacePath);
        }
    }

    private void CreateWorkspaceEntry(bool isDirectory)
    {
        if (_workspacePath is null)
        {
            MessageBox.Show(this, "Ouvrez d’abord un dossier de travail.", "Aucun dossier ouvert",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var parentDirectory = GetSelectedWorkspaceDirectory();
        if (parentDirectory is null)
        {
            return;
        }

        var kind = isDirectory ? "dossier" : "fichier";
        var name = PromptForName($"Nouveau {kind}", $"Nom du {kind} :");
        if (name is null)
        {
            return;
        }

        if (name is "." or ".." ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowFileError(new ArgumentException("Saisissez un nom simple, sans chemin ni caractère interdit."));
            return;
        }

        var path = Path.Combine(parentDirectory, name);
        try
        {
            if (isDirectory)
            {
                _fileService.CreateDirectory(path);
            }
            else
            {
                _fileService.CreateEmptyFile(path);
            }

            RefreshDirectory(parentDirectory, expand: true);
            if (!isDirectory)
            {
                OpenFile(path);
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
        catch (ArgumentException exception)
        {
            ShowFileError(exception);
        }
        catch (NotSupportedException exception)
        {
            ShowFileError(exception);
        }
    }

    private string? GetSelectedWorkspaceDirectory()
    {
        if (_workspacePath is null)
        {
            return null;
        }

        if (FileTree.SelectedItem is TreeViewItem item && item.Tag is string path)
        {
            if (Directory.Exists(path))
            {
                return path;
            }

            if (File.Exists(path))
            {
                return Path.GetDirectoryName(path);
            }
        }

        return _workspacePath;
    }

    private string? PromptForName(string title, string prompt)
    {
        var input = new TextBox { MinWidth = 300, Margin = new Thickness(12, 6, 12, 12) };
        var dialog = new Window
        {
            Owner = this,
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(12, 12, 12, 0) });
        content.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12)
        };
        var createButton = new Button { Content = "Créer", MinWidth = 80, IsDefault = true };
        var cancelButton = new Button { Content = "Annuler", MinWidth = 80, IsCancel = true };
        createButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(input.Text))
            {
                dialog.DialogResult = true;
            }
        };
        buttons.Children.Add(createButton);
        buttons.Children.Add(cancelButton);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => input.Focus();

        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            _executionCancellation?.Cancel();
            _debugCancellation?.Cancel();
            _debugSession?.Dispose();
            _debugSession = null;
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        try
        {
            var declinedDocuments = new HashSet<EditorDocument>();
            while (true)
            {
                var document = _documents.FirstOrDefault(item =>
                    item.IsDirty && !declinedDocuments.Contains(item));
                if (document is null)
                {
                    break;
                }

                var answer = MessageBox.Show(
                    this,
                    $"Enregistrer les modifications de « {document.DisplayName} » ?",
                    "Fichier modifié",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel)
                {
                    return;
                }

                if (answer == MessageBoxResult.No)
                {
                    declinedDocuments.Add(document);
                    continue;
                }

                SelectDocument(document);
                if (!await SaveDocumentAsync(document))
                {
                    return;
                }
            }

            _allowClose = true;
            Close();
        }
        finally
        {
            _isClosing = false;
        }
    }

    private void NewDocument()
    {
        var editor = CreateEditor();
        var document = new EditorDocument(editor, null);
        editor.Document.TextChanged += (_, _) => HandleDocumentTextChanged(document);

        AddDocument(document);
    }

    private async void OpenFile(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (ArgumentException exception)
        {
            ShowFileError(exception);
            return;
        }
        catch (NotSupportedException exception)
        {
            ShowFileError(exception);
            return;
        }

        var existing = _documents.FirstOrDefault(document =>
            document.FilePath is not null &&
            string.Equals(Path.GetFullPath(document.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectDocument(existing);
            return;
        }

        if (!_openingFiles.Add(fullPath))
        {
            return;
        }

        try
        {
            var text = await _fileService.ReadAllTextAsync(fullPath);
            var textDocument = await CreateTextDocumentAsync(text);
            var editor = CreateEditor();
            editor.Document = textDocument;
            var document = new EditorDocument(editor, fullPath)
            {
                IsLargeFile = textDocument.TextLength > MaximumHighlightedDocumentLength
            };
            editor.Document.TextChanged += (_, _) => HandleDocumentTextChanged(document);
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
        catch (ArgumentException exception)
        {
            ShowFileError(exception);
        }
        finally
        {
            _openingFiles.Remove(fullPath);
        }
    }

    private async Task<TextDocument> CreateTextDocumentAsync(string text)
    {
        Dispatcher.VerifyAccess();
        var document = new TextDocument();
        const int chunkLength = 64 * 1024;
        for (var offset = 0; offset < text.Length; offset += chunkLength)
        {
            var length = Math.Min(chunkLength, text.Length - offset);
            document.Insert(document.TextLength, text.Substring(offset, length));
            if (offset + length < text.Length)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        document.UndoStack.ClearAll();
        return document;
    }

    private TextEditor CreateEditor()
    {
        var editor = new TextEditor
        {
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            WordWrap = false,
            Options =
            {
                ConvertTabsToSpaces = true,
                IndentationSize = 4
            },
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
            document.Editor.SyntaxHighlighting = document.IsLargeFile
                ? null
                : _syntaxHighlighter.GetDefinition(document.FilePath, _isDarkTheme);
        }
    }

    private void HandleDocumentTextChanged(EditorDocument document)
    {
        document.IsDirty = true;
        var isLargeFile = document.Editor.Document.TextLength > MaximumHighlightedDocumentLength;
        var highlightingStateChanged = document.IsLargeFile != isLargeFile;
        document.IsLargeFile = isLargeFile;
        UpdateDocumentTab(document, refreshSyntax: highlightingStateChanged);
    }

    private async Task<bool> SaveCurrentDocumentAsync() =>
        CurrentDocument is not null && await SaveDocumentAsync(CurrentDocument);

    private async Task<bool> SaveDocumentAsync(
        EditorDocument document,
        CancellationToken cancellationToken = default)
    {
        await document.SaveLock.WaitAsync(cancellationToken);
        try
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

            var text = document.Editor.Text;
            var version = document.Editor.Document.Version;
            await _fileService.SaveAsync(path, text, cancellationToken);
            document.FilePath = path;
            document.IsDirty = !ReferenceEquals(document.Editor.Document.Version, version);
            document.IsLargeFile = document.Editor.Document.TextLength > MaximumHighlightedDocumentLength;
            UpdateDocumentTab(document, refreshSyntax: true);
            RefreshDirectory(Path.GetDirectoryName(Path.GetFullPath(path)), expand: true);
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
        catch (ArgumentException exception)
        {
            ShowFileError(exception);
        }
        catch (NotSupportedException exception)
        {
            ShowFileError(exception);
        }
        finally
        {
            document.SaveLock.Release();
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
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var item = new TreeViewItem { Header = string.IsNullOrEmpty(name) ? path : name, Tag = path };
        item.Expanded += Directory_Expanded;
        AttachExplorerContextMenu(item);
        if (showPlaceholder)
        {
            item.Items.Add(new TreeViewItem { Header = string.Empty, Tag = "placeholder" });
        }

        return item;
    }

    private TreeViewItem CreateFileNode(string path)
    {
        var item = new TreeViewItem { Header = Path.GetFileName(path), Tag = path };
        AttachExplorerContextMenu(item);
        return item;
    }

    private void AttachExplorerContextMenu(TreeViewItem item)
    {
        var menu = new ContextMenu();
        var newFileItem = new MenuItem { Header = "Nouveau fichier", Tag = item };
        newFileItem.Click += ExplorerContextMenuItem_Click;
        menu.Items.Add(newFileItem);
        var newFolderItem = new MenuItem { Header = "Nouveau dossier", Tag = item };
        newFolderItem.Click += ExplorerContextMenuItem_Click;
        menu.Items.Add(newFolderItem);
        menu.Items.Add(new Separator());
        var refreshItem = new MenuItem { Header = "Actualiser", Tag = item };
        refreshItem.Click += ExplorerContextMenuItem_Click;
        menu.Items.Add(refreshItem);
        item.ContextMenu = menu;
        item.ContextMenuOpening += (_, _) => item.IsSelected = true;
    }

    private void ExplorerContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not MenuItem menuItem || menuItem.Tag is not TreeViewItem item)
        {
            return;
        }

        item.IsSelected = true;
        switch (menuItem.Header?.ToString())
        {
            case "Nouveau fichier":
                CreateWorkspaceEntry(isDirectory: false);
                break;
            case "Nouveau dossier":
                CreateWorkspaceEntry(isDirectory: true);
                break;
            case "Actualiser":
                var path = item.Tag as string;
                var directory = path is not null && Directory.Exists(path)
                    ? path
                    : path is null ? null : Path.GetDirectoryName(path);
                RefreshDirectory(directory);
                break;
        }
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
                parent.Items.Add(CreateFileNode(file));
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

    private void RefreshDirectory(string? directory, bool? expand = null)
    {
        if (directory is null)
        {
            return;
        }

        var item = FindDirectoryNode(FileTree, directory);
        if (item is not null)
        {
            var wasExpanded = item.IsExpanded;
            PopulateDirectory(item);
            item.IsExpanded = expand ?? wasExpanded;
        }
    }

    private static TreeViewItem? FindDirectoryNode(ItemsControl parent, string path)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is string itemPath &&
                string.Equals(Path.GetFullPath(itemPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            var nested = FindDirectoryNode(item, path);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
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
            : $"{(document.FilePath is null ? "Fichier non enregistré" : Path.GetExtension(document.FilePath))} · {document.Editor.Document.LineCount} lignes" +
              $"{(document.IsLargeFile ? " · coloration désactivée (fichier volumineux)" : string.Empty)}";
    }

    private void RunButtonState(bool isRunning)
    {
        StopButton.IsEnabled = isRunning;
        RunButton.IsEnabled = !isRunning;
        DebugButton.IsEnabled = !isRunning && _debugSession is null && _debugCancellation is null;
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
        for (var lineNumber = 1; lineNumber <= editor.Document.LineCount; lineNumber++)
        {
            var existingLine = editor.Document.GetLineByNumber(lineNumber);
            if (existingLine.DelimiterLength > 0)
            {
                newline = editor.Document.GetText(
                    existingLine.Offset + existingLine.Length,
                    existingLine.DelimiterLength);
                break;
            }
        }

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
