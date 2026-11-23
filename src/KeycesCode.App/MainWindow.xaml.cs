using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using KeycesCode.Core.Models;
using KeycesCode.Execution;
using KeycesCode.Execution.Interfaces;
using KeycesCode.Execution.Models;
using KeycesCode.Settings.KeyBindings;

namespace KeycesCode.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, EditorDocument> _documents =
        new(StringComparer.OrdinalIgnoreCase);
    private string _workspacePath;
    private string _activePanel = "Explorer";
    private readonly DispatcherTimer _autoSaveTimer = new();

    // Membre 4 : terminal intégré et exécution des fichiers
    private readonly ITerminalService _terminal = ExecutionModuleFactory.CreateTerminalService();
    private readonly IRunService _runner = ExecutionModuleFactory.CreateRunService();

    public MainWindow()
    {
        InitializeComponent();
        _workspacePath = FindWorkspacePath();
        LoadExplorer();
        InitializeMember5();
        InitializeExecution();
    }

    // ----- Membre 4 : terminal et exécution -----

    private void InitializeExecution()
    {
        // Les services envoient leurs lignes depuis d'autres threads : on repasse par le Dispatcher
        _terminal.OutputReceived += (_, e) => Dispatcher.InvokeAsync(() => AppendLine(TerminalOutput, e.Line));
        _runner.OutputReceived += (_, e) => Dispatcher.InvokeAsync(() => AppendLine(_runTarget, e.Line));

        Closed += async (_, _) =>
        {
            await _runner.StopCurrentProcessAsync();
            await _terminal.StopSessionAsync();
        };

        _ = StartTerminalAsync();
    }

    private TextBox _runTarget = null!;   // Output (exécution) ou Debug Console (débogage)

    private async Task StartTerminalAsync()
    {
        TerminalOutput.Clear();
        try
        {
            await _terminal.StartSessionAsync(_workspacePath);
        }
        catch (Exception ex)
        {
            AppendLine(TerminalOutput, $"[Erreur] Impossible de démarrer le terminal : {ex.Message}");
        }
    }

    private static void AppendLine(TextBox box, string line)
    {
        box.AppendText(line + Environment.NewLine);
        box.ScrollToEnd();
    }

    private async void TerminalInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        string command = TerminalInput.Text;
        TerminalInput.Clear();   // PowerShell réaffiche lui-même la commande tapée
        try
        {
            await _terminal.SendInputAsync(command);
        }
        catch (Exception ex)
        {
            AppendLine(TerminalOutput, $"[Erreur] {ex.Message}");
        }
    }

    private void ShowBottomTab(Button tab) => BottomPanelTab_Click(tab, new RoutedEventArgs());

    private void ShowTerminal()
    {
        ShowBottomTab(TerminalTab);
        TerminalInput.Focus();
    }

    private void ShowTerminal_Click(object sender, RoutedEventArgs e) => ShowTerminal();

    private void ClearTerminal_Click(object sender, RoutedEventArgs e) => TerminalOutput.Clear();

    private async void RestartTerminal_Click(object sender, RoutedEventArgs e)
    {
        await _terminal.StopSessionAsync();
        await StartTerminalAsync();
        ShowTerminal();
    }

    private async void RunFile_Click(object sender, RoutedEventArgs e) => await RunCurrentFileAsync(debug: false);

    private async void DebugFile_Click(object sender, RoutedEventArgs e) => await RunCurrentFileAsync(debug: true);

    private async void StopRun_Click(object sender, RoutedEventArgs e) => await _runner.StopCurrentProcessAsync();

    // Enregistre le fichier ouvert puis l'exécute (Python, JavaScript, C#...) avec le service du Membre 4
    private async Task RunCurrentFileAsync(bool debug)
    {
        if (EditorTabs.SelectedItem is not TabItem { Tag: string path } ||
            !_documents.TryGetValue(path, out EditorDocument? document))
        {
            StatusText.Text = "Ouvre d'abord un fichier à exécuter.";
            return;
        }

        if (_runner.IsRunning)
        {
            StatusText.Text = "Une exécution est déjà en cours (Shift+F5 pour l'arrêter).";
            return;
        }

        if (document.IsDirty) SaveDocument(document);

        _runTarget = debug ? DebugConsoleContent : OutputContent;
        _runTarget.Clear();
        ShowBottomTab(debug ? DebugConsoleTab : OutputTab);
        StatusText.Text = debug ? $"Débogage de {Path.GetFileName(path)}..." : $"Exécution de {Path.GetFileName(path)}...";

        try
        {
            var request = new ExecutionRequest(path, Path.GetDirectoryName(path));
            if (debug) await _runner.StartDebugAsync(request);
            else await _runner.RunAsync(request);
            StatusText.Text = "Exécution terminée";
        }
        catch (Exception ex)
        {
            // Outil manquant (python, node...), fichier introuvable, etc.
            AppendLine(_runTarget, $"[Erreur] {ex.Message}");
            StatusText.Text = "Échec de l'exécution";
        }
    }

    private void ViewMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string panel }) ShowPanel(panel);
    }

    // ----- Membre 5 : Paramètres, raccourcis, Git/GitHub et assistant IA -----

    private void InitializeMember5()
    {
        SettingsEditor.Initialize(App.Settings, App.Themes);
        SettingsEditor.InitializeShortcuts(App.KeyBindings);

        AssistantView.Initialize(App.Assistant, () => App.Settings.Current.AssistantModel);
        App.Settings.SettingsChanged += (_, _) => _ = AssistantView.CheckStatusAsync();

        RegisterCommands();
        _ = SourceControlPanel.OpenFolderAsync(_workspacePath);

        // Les paramètres agissent sur l'éditeur : police, taille, enregistrement automatique
        _autoSaveTimer.Tick += (_, _) => SaveAllDocuments();
        App.Settings.SettingsChanged += (_, _) => ApplyEditorSettings();
        App.Themes.ThemeChanged += (_, _) => RefreshThemeColors();
        ApplyEditorSettings();
    }

    // Applique police, taille et enregistrement automatique à tous les fichiers ouverts
    private void ApplyEditorSettings()
    {
        var settings = App.Settings.Current;
        foreach (var document in _documents.Values)
        {
            document.Editor.FontFamily = new FontFamily(settings.FontFamily);
            document.Editor.FontSize = settings.FontSize;
        }

        _autoSaveTimer.Stop();
        if (settings.AutoSave)
        {
            _autoSaveTimer.Interval = TimeSpan.FromSeconds(settings.AutoSaveDelaySeconds);
            _autoSaveTimer.Start();
        }
    }

    private void SaveAllDocuments()
    {
        foreach (var document in _documents.Values.Where(d => d.IsDirty).ToList())
            SaveDocument(document);
    }

    // Quand on change de thème : on recolore ce qui a été créé par le code
    private void RefreshThemeColors()
    {
        ShowPanel(_activePanel);

        foreach (Button tab in BottomPanelTabs.Children.OfType<Button>())
        {
            bool active = tab.Tag?.ToString()?.StartsWith("Active:", StringComparison.Ordinal) == true;
            tab.Foreground = (Brush)FindResource(active ? "AccentBrush" : "SecondaryText");
        }

        foreach (var document in _documents.Values)
            ApplySyntaxHighlight(document.Editor, document.Path);
    }

    // Les commandes de l'IDE et leur raccourci par défaut (modifiables dans les Paramètres)
    private void RegisterCommands()
    {
        var keys = App.KeyBindings;

        keys.Register(new CommandInfo("file.save", "Enregistrer le fichier", "Ctrl+S"),
            SaveCurrentDocument);
        keys.Register(new CommandInfo("view.settings", "Ouvrir les paramètres", "Ctrl+,"),
            ShowSettings);
        keys.Register(new CommandInfo("view.explorer", "Afficher l'explorateur", "Ctrl+Shift+E"),
            () => ShowPanel("Explorer"));
        keys.Register(new CommandInfo("view.search", "Afficher la recherche", "Ctrl+Shift+F"),
            () => ShowPanel("Search"));
        keys.Register(new CommandInfo("view.git", "Afficher le contrôle de code source", "Ctrl+Shift+G"),
            () => ShowPanel("Source Control"));
        keys.Register(new CommandInfo("view.assistant", "Afficher l'assistant IA", "Ctrl+Shift+I"),
            () => ShowPanel("Assistant"));
        keys.Register(new CommandInfo("run.file", "Exécuter le fichier", "Ctrl+F5"),
            async () => await RunCurrentFileAsync(debug: false));
        keys.Register(new CommandInfo("run.debug", "Déboguer le fichier", "F5"),
            async () => await RunCurrentFileAsync(debug: true));
        keys.Register(new CommandInfo("run.stop", "Arrêter l'exécution", "Shift+F5"),
            async () => await _runner.StopCurrentProcessAsync());
        keys.Register(new CommandInfo("terminal.show", "Afficher le terminal", "Ctrl+Shift+T"),
            ShowTerminal);
        keys.Register(new CommandInfo("git.refresh", "Git : actualiser", "Ctrl+Shift+R"),
            async () => await SourceControlPanel.RefreshCommandAsync());
        keys.Register(new CommandInfo("git.pull", "Git : pull", "Ctrl+Alt+L"),
            async () => await SourceControlPanel.PullCommandAsync());
        keys.Register(new CommandInfo("git.push", "Git : push", "Ctrl+Alt+P"),
            async () => await SourceControlPanel.PushCommandAsync());
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsHost.Visibility == Visibility.Visible) HideSettings();
        else ShowSettings();
    }

    private void ShowSettings()
    {
        SettingsEditor.Reload();
        SettingsHost.Visibility = Visibility.Visible;
        StatusText.Text = "Paramètres";
    }

    private void HideSettings() => SettingsHost.Visibility = Visibility.Collapsed;

    private void SourceControlPanel_BranchChanged(object? sender, string branch) =>
        BranchStatus.Text = string.IsNullOrEmpty(branch) ? "" : $"⎇ {branch}";

    // Affiche un panneau de la barre latérale comme si on avait cliqué sur son icône
    private void ShowPanel(string panelName)
    {
        var button = ActivityBar.Children.OfType<Button>()
            .FirstOrDefault(b => b.Tag as string == panelName);
        if (button is not null)
            ActivityButton_Click(button, new RoutedEventArgs());
    }

    private void ActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string panelName)
        {
            return;
        }

        ExplorerPanel.Visibility = panelName == "Explorer" ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = panelName == "Search" ? Visibility.Visible : Visibility.Collapsed;
        SourceControlPanel.Visibility = panelName == "Source Control" ? Visibility.Visible : Visibility.Collapsed;
        AssistantView.Visibility = panelName == "Assistant" ? Visibility.Visible : Visibility.Collapsed;

        foreach (Button activityButton in ActivityBar.Children.OfType<Button>())
        {
            activityButton.Foreground = (Brush)FindResource("SecondaryText");
        }

        button.Foreground = (Brush)FindResource("AccentBrush");
        _activePanel = panelName;

        if (panelName == "Source Control")
        {
            _ = SourceControlPanel.RefreshCommandAsync();
        }
    }

    private void LoadExplorer()
    {
        WorkspaceLabel.Text = Path.GetFileName(_workspacePath.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(WorkspaceLabel.Text))
        {
            WorkspaceLabel.Text = _workspacePath;
        }

        WorkspaceLabel.ToolTip = _workspacePath;
        ExplorerTree.Items.Clear();

        var root = CreateDirectoryNode(_workspacePath);
        root.IsExpanded = true;
        ExplorerTree.Items.Add(root);
        PopulateDirectoryNode(root);
    }

    private static DockPanel CreateExplorerHeader(string name, bool isDirectory, string? extension)
    {
        var header = new DockPanel
        {
            LastChildFill = true,
            MinHeight = 20
        };

        var icon = new TextBlock
        {
            Width = 18,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 11
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");

        if (isDirectory)
        {
            icon.Text = "\u25A1";
            icon.SetResourceReference(TextBlock.ForegroundProperty, "FolderIcon");
        }
        else
        {
            (icon.Text, icon.Foreground) = GetFileIcon(extension);
        }

        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        var label = new TextBlock
        {
            Text = name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryText");
        header.Children.Add(label);
        return header;
    }

    private static (string Text, Brush Foreground) GetFileIcon(string? extension)
    {
        return extension switch
        {
            ".cs" => ("C#", new SolidColorBrush(Color.FromRgb(103, 185, 107))),
            ".xaml" => ("X", new SolidColorBrush(Color.FromRgb(86, 156, 214))),
            ".xml" or ".axml" or ".csproj" or ".props" or ".targets" => ("X", new SolidColorBrush(Color.FromRgb(86, 156, 214))),
            ".js" or ".jsx" => ("JS", new SolidColorBrush(Color.FromRgb(241, 196, 15))),
            ".ts" or ".tsx" => ("TS", new SolidColorBrush(Color.FromRgb(49, 120, 198))),
            ".json" => ("{}", new SolidColorBrush(Color.FromRgb(241, 196, 15))),
            ".css" or ".scss" or ".less" => ("#", new SolidColorBrush(Color.FromRgb(86, 156, 214))),
            ".html" or ".htm" => ("<>", new SolidColorBrush(Color.FromRgb(227, 76, 38))),
            ".py" => ("PY", new SolidColorBrush(Color.FromRgb(53, 114, 165))),
            ".md" or ".markdown" => ("M", new SolidColorBrush(Color.FromRgb(156, 220, 254))),
            ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" => ("C+", new SolidColorBrush(Color.FromRgb(86, 156, 214))),
            ".go" => ("GO", new SolidColorBrush(Color.FromRgb(78, 201, 176))),
            ".rs" => ("RS", new SolidColorBrush(Color.FromRgb(206, 145, 120))),
            ".php" => ("PHP", new SolidColorBrush(Color.FromRgb(136, 146, 191))),
            ".java" => ("JV", new SolidColorBrush(Color.FromRgb(241, 101, 41))),
            ".rb" => ("RB", new SolidColorBrush(Color.FromRgb(204, 52, 45))),
            ".sh" or ".bash" or ".ps1" or ".bat" or ".cmd" => ("$", new SolidColorBrush(Color.FromRgb(137, 209, 133))),
            ".sql" => ("DB", new SolidColorBrush(Color.FromRgb(206, 145, 120))),
            ".yml" or ".yaml" or ".toml" or ".ini" => ("Y", new SolidColorBrush(Color.FromRgb(206, 145, 120))),
            _ => ("·", new SolidColorBrush(Color.FromRgb(153, 153, 153)))
        };
    }

    private static TreeViewItem CreateDirectoryNode(string path)
    {
        string name = Path.GetFileName(path.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var node = new TreeViewItem
        {
            Header = CreateExplorerHeader(name, isDirectory: true, extension: null),
            Tag = path
        };
        if (string.IsNullOrEmpty(name))
        {
            node.Header = CreateExplorerHeader(path, isDirectory: true, extension: null);
        }

        node.Expanded += DirectoryNode_Expanded;
        return node;
    }

    private static void DirectoryNode_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem node || node.Tag is not string path)
        {
            return;
        }

        if (node.Items.Count == 1 && node.Items[0] is TreeViewItem placeholder &&
            placeholder.Tag is null)
        {
            node.Items.Clear();
            PopulateDirectoryNode(node);
        }
    }

    private static void PopulateDirectoryNode(TreeViewItem node)
    {
        if (node.Tag is not string path)
        {
            return;
        }

        node.Items.Clear();
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(path)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var directoryNode = CreateDirectoryNode(directory);
                directoryNode.Items.Add(new TreeViewItem());
                node.Items.Add(directoryNode);
            }

            foreach (string file in Directory.EnumerateFiles(path)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(file);
                string extension = Path.GetExtension(file).ToLowerInvariant();
                var fileNode = new TreeViewItem
                {
                    Header = CreateExplorerHeader(name, isDirectory: false, extension),
                    Tag = file
                };
                fileNode.MouseDoubleClick += FileNode_MouseDoubleClick;
                node.Items.Add(fileNode);
            }
        }
        catch (UnauthorizedAccessException)
        {
            node.Items.Add(new TreeViewItem
            {
                Header = "(Access denied)",
                IsEnabled = false
            });
        }
        catch (IOException)
        {
            node.Items.Add(new TreeViewItem
            {
                Header = "(Unable to read folder)",
                IsEnabled = false
            });
        }
    }

    private static void FileNode_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem { Tag: string path } && File.Exists(path))
        {
            if (Application.Current.MainWindow is MainWindow window)
            {
                window.OpenDocument(path);
            }
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Open Workspace Folder",
            InitialDirectory = _workspacePath
        };

        if (dialog.ShowDialog(this) == true)
        {
            _workspacePath = dialog.FolderName;
            LoadExplorer();
            _ = SourceControlPanel.OpenFolderAsync(_workspacePath);
            _ = _terminal.ChangeDirectoryAsync(_workspacePath);
            StatusText.Text = $"Workspace: {_workspacePath}";
        }
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        string? name = ShowNameDialog("New File", "File name:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string targetDirectory = GetTargetDirectory();
        string filePath = GetAvailablePath(targetDirectory, name.Trim(), isDirectory: false);

        try
        {
            File.WriteAllText(filePath, string.Empty);
            LoadExplorer();
            OpenDocument(filePath);
            StatusText.Text = $"Created {filePath}";
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to Create File",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to Create File",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        string? name = ShowNameDialog("New Folder", "Folder name:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string targetDirectory = GetTargetDirectory();
        string folderPath = GetAvailablePath(targetDirectory, name.Trim(), isDirectory: true);

        try
        {
            Directory.CreateDirectory(folderPath);
            LoadExplorer();
            StatusText.Text = $"Created {folderPath}";
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to Create Folder",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to Create Folder",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshExplorer_Click(object sender, RoutedEventArgs e)
    {
        LoadExplorer();
        StatusText.Text = "Explorer refreshed.";
    }

    private void CollapseFolders_Click(object sender, RoutedEventArgs e)
    {
        CollapseFolders(ExplorerTree);
        StatusText.Text = "All folders collapsed.";
    }

    private string GetTargetDirectory()
    {
        if (ExplorerTree.SelectedItem is TreeViewItem selectedItem &&
            selectedItem.Tag is string selectedPath)
        {
            if (Directory.Exists(selectedPath))
            {
                return selectedPath;
            }

            if (File.Exists(selectedPath))
            {
                return Path.GetDirectoryName(selectedPath) ?? _workspacePath;
            }
        }

        return _workspacePath;
    }

    private static string GetAvailablePath(string directory, string name, bool isDirectory)
    {
        string basePath = Path.Combine(directory, name);
        if (!(isDirectory ? Directory.Exists(basePath) : File.Exists(basePath)))
        {
            return basePath;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = isDirectory ? string.Empty : Path.GetExtension(name);
        int counter = 2;
        string candidate;

        do
        {
            candidate = Path.Combine(directory, $"{stem} {counter}{extension}");
            counter++;
        }
        while (isDirectory ? Directory.Exists(candidate) : File.Exists(candidate));

        return candidate;
    }

    private static void CollapseFolders(ItemsControl items)
    {
        foreach (object item in items.Items)
        {
            if (item is not TreeViewItem treeItem)
            {
                continue;
            }

            treeItem.IsExpanded = false;
            CollapseFolders(treeItem);
        }
    }

    private string? ShowNameDialog(string title, string prompt)
    {
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Width = 420,
            Height = 150,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("AppBackground"),
            BorderBrush = (Brush)FindResource("StrongBorderBrush"),
            BorderThickness = new Thickness(1),
            WindowStyle = WindowStyle.ToolWindow
        };

        var grid = new Grid
        {
            Margin = new Thickness(16)
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var promptText = new TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, 7)
        };
        var input = new TextBox
        {
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Width = 82,
            Margin = new Thickness(0, 0, 8, 0),
            Background = (Brush)FindResource("HoverBackground"),
            BorderBrush = (Brush)FindResource("StrongBorderBrush")
        };
        var create = new Button
        {
            Content = "Create",
            Width = 82,
            IsDefault = true
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(create);

        Grid.SetRow(promptText, 0);
        Grid.SetRow(input, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(promptText);
        grid.Children.Add(input);
        grid.Children.Add(buttons);
        dialog.Content = grid;

        cancel.Click += (_, _) =>
        {
            dialog.DialogResult = false;
        };
        create.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(input.Text))
            {
                dialog.DialogResult = true;
            }
        };

        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchFiles();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SearchFiles();
            e.Handled = true;
        }
    }

    private void SearchFiles()
    {
        string query = SearchBox.Text.Trim();
        SearchResults.Items.Clear();
        if (query.Length == 0)
        {
            StatusText.Text = "Enter a file name to search.";
            return;
        }

        int inaccessibleFolders = 0;
        SearchDirectory(_workspacePath, query, inaccessibleFolders: ref inaccessibleFolders);
        StatusText.Text = SearchResults.Items.Count == 0
            ? $"No files matched \"{query}\"."
            : $"{SearchResults.Items.Count} file(s) found.";
        if (inaccessibleFolders > 0)
        {
            StatusText.Text += $" {inaccessibleFolders} folder(s) could not be searched.";
        }
    }

    private void SearchDirectory(string directory, string query, ref int inaccessibleFolders)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    SearchResults.Items.Add(file);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            inaccessibleFolders++;
            return;
        }
        catch (IOException)
        {
            inaccessibleFolders++;
            return;
        }

        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(directory).ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            inaccessibleFolders++;
            return;
        }
        catch (IOException)
        {
            inaccessibleFolders++;
            return;
        }

        foreach (string subdirectory in subdirectories)
        {
            SearchDirectory(subdirectory, query, ref inaccessibleFolders);
        }
    }

    private void SearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResults.SelectedItem is string path)
        {
            OpenDocument(path);
        }
    }

    private void OpenDocument(string path)
    {
        HideSettings();
        string fullPath = Path.GetFullPath(path);
        if (_documents.TryGetValue(fullPath, out EditorDocument? existingDocument))
        {
            EditorTabs.SelectedItem = existingDocument.Tab;
            return;
        }

        try
        {
            string content = File.ReadAllText(fullPath);
            var editor = new RichTextBox
            {
                // Police et taille choisies dans les Paramètres
                FontFamily = new FontFamily(App.Settings.Current.FontFamily),
                FontSize = App.Settings.Current.FontSize,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                IsDocumentEnabled = false
            };
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                LineHeight = 20
            };
            paragraph.Inlines.Add(new Run(content));
            // SetResourceReference : la couleur suit le thème (sombre / clair)
            editor.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
            editor.SetResourceReference(Control.BackgroundProperty, "EditorBackground");
            editor.SetResourceReference(TextBoxBase.SelectionBrushProperty, "TextSelection");
            editor.SetResourceReference(TextBoxBase.CaretBrushProperty, "PrimaryText");
            editor.Document = new FlowDocument(paragraph)
            {
                PagePadding = new Thickness(0)
            };
            editor.Document.SetResourceReference(FlowDocument.ForegroundProperty, "PrimaryText");
            ApplySyntaxHighlight(editor, fullPath);

            var title = new TextBlock
            {
                Text = Path.GetFileName(fullPath),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 6, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 165
            };
            var closeButton = new Button
            {
                Content = "\u00D7",
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 15,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Close"
            };
            closeButton.SetResourceReference(Control.ForegroundProperty, "SecondaryText");
            var header = new DockPanel
            {
                LastChildFill = true,
                MinHeight = 35
            };
            DockPanel.SetDock(closeButton, Dock.Right);
            header.Children.Add(closeButton);
            header.Children.Add(title);

            var tab = new TabItem
            {
                Header = header,
                Content = editor,
                Tag = fullPath
            };
            var document = new EditorDocument(fullPath, tab, editor, title);
            closeButton.Click += (_, _) => CloseDocument(document);
            editor.TextChanged += (_, _) =>
            {
                if (editor.Tag is true)
                {
                    return;
                }
                document.IsDirty = true;
                UpdateDocumentTitle(document);
                ApplySyntaxHighlight(editor, document.Path);
            };

            _documents.Add(fullPath, document);
            EditorTabs.Items.Add(tab);
            EditorTabs.SelectedItem = tab;
            StatusText.Text = fullPath;
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to Open File",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to Open File",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseDocument(EditorDocument document)
    {
        _documents.Remove(document.Path);
        EditorTabs.Items.Remove(document.Tab);
        if (EditorTabs.Items.Count == 0)
        {
            WelcomePanel.Visibility = Visibility.Visible;
        }
    }

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, EditorTabs))
        {
            WelcomePanel.Visibility = EditorTabs.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void SaveDocument_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentDocument();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Pendant que la page Paramètres attend un nouveau raccourci, on ne fait rien
        if (SettingsEditor.IsCapturingShortcut) return;

        var shortcut = KeyReader.Read(e);
        if (shortcut is not null && App.KeyBindings.TryExecute(shortcut))
        {
            e.Handled = true;
        }
    }

    private void SaveCurrentDocument()
    {
        if (EditorTabs.SelectedItem is not TabItem tab ||
            tab.Tag is not string path ||
            !_documents.TryGetValue(path, out EditorDocument? document))
        {
            return;
        }

        SaveDocument(document);
    }

    private void SaveDocument(EditorDocument document)
    {
        string path = document.Path;
        try
        {
            var textRange = new TextRange(
                document.Editor.Document.ContentStart,
                document.Editor.Document.ContentEnd);
            File.WriteAllText(path, textRange.Text);
            document.IsDirty = false;
            UpdateDocumentTitle(document);
            StatusText.Text = $"Saved {path}";
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to Save File",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to Save File",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void UpdateDocumentTitle(EditorDocument document)
    {
        document.Title.Text = Path.GetFileName(document.Path) + (document.IsDirty ? " *" : string.Empty);
    }

    private void BottomPanelTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button selectedTab)
        {
            return;
        }

        ProblemsContent.Visibility = Visibility.Collapsed;
        OutputContent.Visibility = Visibility.Collapsed;
        DebugConsoleContent.Visibility = Visibility.Collapsed;
        TerminalContent.Visibility = Visibility.Collapsed;

        foreach (Button tab in BottomPanelTabs.Children.OfType<Button>())
        {
            tab.Tag = tab.Tag?.ToString()?.Replace("Active:", "", StringComparison.Ordinal);
            tab.Foreground = (Brush)FindResource("SecondaryText");
        }

        switch (selectedTab.Tag?.ToString())
        {
            case "Problems":
                ProblemsContent.Visibility = Visibility.Visible;
                break;
            case "Output":
                OutputContent.Visibility = Visibility.Visible;
                break;
            case "Debug Console":
                DebugConsoleContent.Visibility = Visibility.Visible;
                break;
            default:
                TerminalContent.Visibility = Visibility.Visible;
                break;
        }

        selectedTab.Tag = $"Active:{selectedTab.Tag}";
        selectedTab.Foreground = (Brush)FindResource("AccentBrush");
        StatusText.Text = $"{selectedTab.Tag.ToString()?.Split(':')[1]} panel selected";
    }

    private static string FindWorkspacePath()
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        string fallback = directory.FullName;

        while (directory is not null)
        {
            if (directory.GetFiles("*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
            {
                return directory.FullName;
            }

            string projectSubdirectory = Path.Combine(directory.FullName, "Keyce-code");
            if (Directory.Exists(projectSubdirectory) &&
                Directory.GetFiles(projectSubdirectory, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
            {
                return projectSubdirectory;
            }

            directory = directory.Parent;
        }

        return fallback;
    }

    private static void ApplySyntaxHighlight(RichTextBox editor, string path)
    {
        if (editor.Tag is true)
        {
            return;
        }

        editor.Tag = true;
        try
        {
        var textRange = new TextRange(
            editor.Document.ContentStart,
            editor.Document.ContentEnd);
        string text = textRange.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        textRange.ApplyPropertyValue(TextElement.ForegroundProperty,
            (Brush)Application.Current.FindResource("PrimaryText"));

        string extension = Path.GetExtension(path).ToLowerInvariant();
        string language = extension switch
        {
            ".cs" => "csharp",
            ".xaml" or ".xml" or ".axml" or ".csproj" => "markup",
            ".js" or ".ts" or ".jsx" or ".tsx" or ".java" or ".go" or ".rs" or ".rb" or ".php" or ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hpp" or ".sql" => "script",
            ".json" => "json",
            ".css" or ".scss" or ".less" => "css",
            ".html" or ".htm" => "markup",
            ".py" => "python",
            ".sh" or ".bash" or ".ps1" or ".bat" or ".cmd" => "shell",
            ".yml" or ".yaml" or ".toml" or ".ini" => "yaml",
            ".md" or ".markdown" => "markdown",
            _ => "text"
        };

        if (language == "text")
        {
            return;
        }

        var commentColor = (Brush)Application.Current.FindResource("MutedText");
        // Couleurs de la coloration : une palette pour chaque thème
        bool light = App.Themes.CurrentThemeId == "light";
        var stringColor = new SolidColorBrush(light ? Color.FromRgb(163, 21, 21) : Color.FromRgb(206, 145, 120));
        var keywordColor = new SolidColorBrush(light ? Color.FromRgb(175, 0, 219) : Color.FromRgb(197, 134, 192));
        var typeColor = new SolidColorBrush(light ? Color.FromRgb(38, 127, 153) : Color.FromRgb(78, 201, 176));
        var numberColor = new SolidColorBrush(light ? Color.FromRgb(9, 134, 88) : Color.FromRgb(181, 206, 168));
        var tagColor = new SolidColorBrush(light ? Color.FromRgb(128, 0, 0) : Color.FromRgb(86, 156, 214));
        var attributeColor = new SolidColorBrush(light ? Color.FromRgb(229, 0, 0) : Color.FromRgb(156, 220, 254));

        var protectedRanges = new List<(int Start, int Length)>();

        if (language == "markup")
        {
            ApplyRegexHighlight(editor, text, @"<!--.*?-->", commentColor);
            ApplyRegexHighlight(editor, text, @"(?<=<[/!?]?)?[A-Za-z_][A-Za-z0-9_.:-]*(?=\s|>|/>)", tagColor);
            ApplyRegexHighlight(editor, text, @"(?<=\s)[A-Za-z_:][A-Za-z0-9_.:-]*(?=\s*=)", attributeColor);
            ApplyRegexHighlight(editor, text, "\"[^\"]*\"|'[^']*'", stringColor);
            ApplyRegexHighlight(editor, text, @"&[A-Za-z]+;", numberColor);
            return;
        }

        if (language == "markdown")
        {
            ApplyRegexHighlight(editor, text, @"^#{1,6}\s.*$", tagColor, RegexOptions.Multiline);
            ApplyRegexHighlight(editor, text, @"`[^`]+`", stringColor);
            ApplyRegexHighlight(editor, text, @"\bhttps?://\S+", attributeColor);
            return;
        }

        string commentPattern = language switch
        {
            "python" or "shell" or "yaml" => @"#[^\r\n]*",
            "css" => @"//[^\r\n]*|/\*.*?\*/",
            "markup" => @"<!--.*?-->",
            _ => @"//[^\r\n]*|/\*.*?\*/"
        };
        ApplyRegexHighlight(editor, text, commentPattern, commentColor,
            RegexOptions.Multiline | RegexOptions.Singleline);
        protectedRanges.AddRange(GetMatchRanges(
            text,
            commentPattern,
            RegexOptions.Multiline | RegexOptions.Singleline));

        string stringPattern = language switch
        {
            "python" => @"@?('''.*?'''|"""".*?""""|'(?:\\.|[^'])*'|""(?:\\.|[^""])*"")",
            "json" => @"""(?:\\.|[^""])*""",
            "css" => @"""(?:\\.|[^""])*""|'(?:\\.|[^'])*'",
            _ => @"@?" + "\"" + "(?:\\\\.|[^\\\"])*" + "\""
        };
        var options = RegexOptions.Singleline;
        if (language == "json")
        {
            ApplyRegexHighlight(editor, text, stringPattern, stringColor, options);
            protectedRanges.AddRange(GetMatchRanges(text, stringPattern, options));
        }
        else
        {
            ApplyRegexHighlight(editor, text, stringPattern, stringColor, options);
            protectedRanges.AddRange(GetMatchRanges(text, stringPattern, options));
        }

        string keywordPattern = language switch
        {
            "csharp" => @"\b(abstract|as|async|await|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|get|goto|if|implicit|in|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|private|protected|public|readonly|record|ref|required|return|sbyte|sealed|set|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|virtual|void|volatile|where|while|yield)\b",
            "python" => @"\b(and|as|assert|async|await|break|class|continue|def|del|elif|else|except|False|finally|for|from|global|if|import|in|is|lambda|None|not|or|pass|raise|return|True|try|while|with|yield)\b",
            "script" => @"\b(await|break|case|catch|class|const|continue|debugger|default|delete|do|else|export|extends|false|finally|for|from|function|get|if|implements|import|in|instanceof|interface|let|new|null|package|private|protected|public|return|set|static|super|switch|this|throw|true|try|typeof|undefined|var|void|while|with|yield)\b",
            "shell" => @"\b(if|then|elif|else|fi|for|in|do|done|while|until|case|esac|function|return|exit|export|readonly|local|declare|true|false)\b",
            "yaml" => @"\b(true|false|null|yes|no|on|off)\b",
            "json" => @"\b(true|false|null)\b",
            "css" => @"\b(!important|inherit|initial|unset|block|inline|flex|grid|none|relative|absolute|fixed)\b",
            _ => @"(?!)"
        };
        ApplyRegexHighlight(editor, text, keywordPattern, keywordColor,
            RegexOptions.None, protectedRanges);

        string typePattern = language == "csharp"
            ? @"\b[A-Z][A-Za-z0-9_]*\b"
            : language == "python"
                ? @"\b[A-Z][A-Za-z0-9_]*\b"
                : language == "script"
                    ? @"\b(?:auto|bool|char|double|float|int|long|short|signed|unsigned|void|string|object|struct|enum|interface|func|fn|mut|pub|crate)\b"
                    : @"(?<![A-Za-z0-9_-])--?[A-Za-z][A-Za-z0-9_-]*";
        ApplyRegexHighlight(editor, text, typePattern, typeColor,
            RegexOptions.None, protectedRanges);

        ApplyRegexHighlight(editor, text, @"\b(?:0[xX][0-9A-Fa-f]+|\d+(?:\.\d+)?)\b",
            numberColor, RegexOptions.None, protectedRanges);
        }
        finally
        {
            editor.Tag = false;
        }
    }

    private static IEnumerable<(int Start, int Length)> GetMatchRanges(
        string text,
        string pattern,
        RegexOptions options)
    {
        return Regex.Matches(text, pattern, options)
            .Cast<Match>()
            .Select(match => (match.Index, match.Length));
    }

    private static void ApplyRegexHighlight(
        RichTextBox editor,
        string text,
        string pattern,
        Brush color,
        RegexOptions options = RegexOptions.None,
        IEnumerable<(int Start, int Length)>? excludedRanges = null)
    {
        var exclusions = excludedRanges?.ToArray() ?? Array.Empty<(int Start, int Length)>();
        foreach (Match match in Regex.Matches(text, pattern, options))
        {
            if (exclusions.Any(range =>
                    match.Index >= range.Start &&
                    match.Index < range.Start + range.Length))
            {
                continue;
            }

            TextPointer start = editor.Document.ContentStart.GetPositionAtOffset(match.Index);
            TextPointer end = editor.Document.ContentStart.GetPositionAtOffset(match.Index + match.Length);
            if (start is not null && end is not null)
            {
                new TextRange(start, end).ApplyPropertyValue(TextElement.ForegroundProperty, color);
            }
        }
    }

    private sealed class EditorDocument
    {
        public EditorDocument(string path, TabItem tab, RichTextBox editor, TextBlock title)
        {
            Path = path;
            Tab = tab;
            Editor = editor;
            Title = title;
        }

        public string Path { get; }
        public TabItem Tab { get; }
        public RichTextBox Editor { get; }
        public TextBlock Title { get; }
        public bool IsDirty { get; set; }
    }
}
