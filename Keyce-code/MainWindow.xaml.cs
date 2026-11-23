using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Keyce_code;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, EditorDocument> _documents =
        new(StringComparer.OrdinalIgnoreCase);
    private string _workspacePath;

    public MainWindow()
    {
        InitializeComponent();
        _workspacePath = FindWorkspacePath();
        LoadExplorer();
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
        ExtensionsPanel.Visibility = panelName == "Extensions" ? Visibility.Visible : Visibility.Collapsed;

        foreach (Button activityButton in ActivityBar.Children.OfType<Button>())
        {
            activityButton.Foreground = (Brush)FindResource("SecondaryText");
        }

        button.Foreground = (Brush)FindResource("AccentBrush");

        if (panelName == "Source Control")
        {
            _ = RefreshSourceControlAsync();
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

    private static TreeViewItem CreateDirectoryNode(string path)
    {
        var node = new TreeViewItem
        {
            Header = Path.GetFileName(path.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Tag = path
        };
        if (string.IsNullOrEmpty(node.Header as string))
        {
            node.Header = path;
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
                var fileNode = new TreeViewItem
                {
                    Header = Path.GetFileName(file),
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
        string fullPath = Path.GetFullPath(path);
        if (_documents.TryGetValue(fullPath, out EditorDocument? existingDocument))
        {
            EditorTabs.SelectedItem = existingDocument.Tab;
            return;
        }

        try
        {
            string content = File.ReadAllText(fullPath);
            var editor = new TextBox
            {
                Text = content,
                AcceptsReturn = true,
                AcceptsTab = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Foreground = (Brush)FindResource("PrimaryText"),
                Background = (Brush)FindResource("EditorBackground"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap
            };

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
                Foreground = (Brush)FindResource("SecondaryText"),
                BorderThickness = new Thickness(0),
                FontSize = 15,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Close"
            };
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
                document.IsDirty = true;
                UpdateDocumentTitle(document);
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
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            SaveCurrentDocument();
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

        try
        {
            File.WriteAllText(path, document.Editor.Text);
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

    private async void RefreshSourceControl_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSourceControlAsync();
    }

    private async Task RefreshSourceControlAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _workspacePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--short");
        startInfo.ArgumentList.Add("--branch");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                SourceControlOutput.Text = "Git could not be started.";
                return;
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string output = await outputTask;
            string error = await errorTask;

            SourceControlOutput.Text = process.ExitCode == 0
                ? string.IsNullOrWhiteSpace(output) ? "No changes." : output
                : string.IsNullOrWhiteSpace(error) ? "Git status failed." : error;
        }
        catch (Win32Exception ex)
        {
            SourceControlOutput.Text = $"Unable to run Git: {ex.Message}";
        }
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

    private sealed class EditorDocument
    {
        public EditorDocument(string path, TabItem tab, TextBox editor, TextBlock title)
        {
            Path = path;
            Tab = tab;
            Editor = editor;
            Title = title;
        }

        public string Path { get; }
        public TabItem Tab { get; }
        public TextBox Editor { get; }
        public TextBlock Title { get; }
        public bool IsDirty { get; set; }
    }
}
