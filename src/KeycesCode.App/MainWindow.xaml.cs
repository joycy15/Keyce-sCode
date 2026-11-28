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
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
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
    // Dossier ouvert ; vide tant qu'aucun dossier n'est ouvert (comme VS Code au démarrage)
    private string _workspacePath = "";
    private bool HasWorkspace => _workspacePath.Length > 0;
    private string _activePanel = "Explorer";
    private readonly DispatcherTimer _autoSaveTimer = new();

    // Membre 4 : terminal intégré et exécution des fichiers
    private readonly ITerminalService _terminal = ExecutionModuleFactory.CreateTerminalService();
    private readonly IRunService _runner = ExecutionModuleFactory.CreateRunService();

    public MainWindow()
    {
        InitializeComponent();
        Window_StateChanged(this, EventArgs.Empty);
        Loaded += (_, _) =>
        {
            // Une fois la fenêtre affichée : trait blanc sur l'icône active, onglet du bas souligné
            ShowPanel(_activePanel);
            var activeTab = BottomPanelTabs.Children.OfType<Button>()
                .FirstOrDefault(b => ReferenceEquals(b.Style, FindResource("BottomPanelTabActive")));
            BottomPanelTab_Click(activeTab ?? TerminalTab, new RoutedEventArgs());
        };
        LoadExplorer();          // au démarrage : aucun dossier, l'explorateur propose d'en ouvrir un
        InitializeExplorerMenu(); // clic droit : renommer, supprimer... (MainWindow.FileActions.cs)
        ExplorerTree.PreviewMouseLeftButtonUp += ExplorerTree_ItemClicked;   // un seul clic, comme VS Code
        InitializeMember5();
        InitializeExecution();
        RefreshRecentList();
    }

    // ----- Dossier de travail et page d'accueil -----

    // Ouvre un dossier dans tout l'IDE : explorateur, Git, terminal, liste des récents
    private async void OpenWorkspace(string path)
    {
        if (!Directory.Exists(path))
        {
            StatusText.Text = $"Dossier introuvable : {path}";
            return;
        }

        _workspacePath = path;
        LoadExplorer();
        ShowPanel("Explorer");
        _ = SourceControlPanel.OpenFolderAsync(path);
        _ = _terminal.ChangeDirectoryAsync(path);
        _terminalCwd = path;
        UpdatePrompt();
        UpdateWindowTitle();
        StatusText.Text = $"Workspace: {path}";

        var recent = new List<string> { path };
        recent.AddRange(App.Settings.Current.RecentFolders
            .Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
        await App.Settings.SaveAsync(App.Settings.Current with { RecentFolders = recent.Take(10).ToList() });
        RefreshRecentList();
    }

    private void CloseFolder_Click(object sender, RoutedEventArgs e)
    {
        _workspacePath = "";
        LoadExplorer();
        SourceControlPanel.CloseFolder();
        UpdateWindowTitle();
        StatusText.Text = "Aucun dossier ouvert";
    }

    private void SourceControlPanel_FolderOpened(object? sender, string folder) => OpenWorkspace(folder);

    private void RefreshRecentList()
    {
        var items = App.Settings.Current.RecentFolders
            .Where(Directory.Exists)
            .Take(6)
            .Select(p => new RecentFolder(
                Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : p, p))
            .ToList();
        RecentList.ItemsSource = items;
        NoRecentText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public sealed record RecentFolder(string Name, string Path);

    private void RecentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) OpenWorkspace(path);
    }

    private void WelcomeNewFile_Click(object sender, RoutedEventArgs e)
    {
        if (HasWorkspace)
        {
            NewFile_Click(sender, e);
            return;
        }

        // Sans dossier ouvert : on choisit où créer le fichier
        var dialog = new SaveFileDialog { Title = "Nouveau fichier", FileName = "sans-titre.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, string.Empty);
            OpenDocument(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Impossible de créer le fichier",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WelcomeOpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Ouvrir un fichier" };
        if (HasWorkspace) dialog.InitialDirectory = _workspacePath;
        if (dialog.ShowDialog(this) == true) OpenDocument(dialog.FileName);
    }

    private void WelcomeClone_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel("Source Control");
        SourceControlPanel.ShowClone();
    }

    private void WelcomeCard_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as Button)?.Tag as string)
        {
            case "Settings": ShowSettings(); break;
            case "Terminal": ShowTerminal(); break;
            case string panel: ShowPanel(panel); break;
        }
    }

    // ----- Membre 4 : terminal et exécution -----

    // Écrivains en couleur pour TERMINAL, OUTPUT et DEBUG CONSOLE
    private ConsoleWriter _terminalWriter = null!;
    private ConsoleWriter _outputWriter = null!;
    private ConsoleWriter _debugWriter = null!;
    private ConsoleWriter _runWriter = null!;   // Output (exécution) ou Debug Console (débogage)

    // Dossier courant du terminal, affiché dans l'invite « PS dossier> »
    private string _terminalCwd = "";

    // Historique des commandes (flèches ↑ / ↓)
    private readonly List<string> _history = [];
    private int _historyIndex;

    // Complétion avec Tab
    private List<string> _tabMatches = [];
    private int _tabIndex;
    private string _tabPrefix = "";

    // Ligne invisible envoyée après chaque commande pour connaître le dossier courant
    private const string CwdMarker = "__KC_CWD__";

    private static readonly System.Text.RegularExpressions.Regex PromptLine =
        new(@"^PS (?<path>.+?)>\s?(?<command>.*)$");

    private void InitializeExecution()
    {
        _terminalWriter = new ConsoleWriter(TerminalOutput, TerminalContent);
        _outputWriter = new ConsoleWriter(OutputContent);
        _debugWriter = new ConsoleWriter(DebugConsoleContent);
        _runWriter = _outputWriter;
        _outputWriter.WriteLine("Aucune sortie pour l'instant. Ctrl+F5 exécute le fichier ouvert.", "MutedText");
        _debugWriter.WriteLine("Console de débogage prête. F5 lance le débogage du fichier ouvert.", "MutedText");

        // Les services envoient leurs lignes depuis d'autres threads : on repasse par le Dispatcher
        // Priorité « Background » : l'écriture et les clics restent fluides même si un programme affiche beaucoup
        _terminal.OutputReceived += (_, e) =>
            Dispatcher.InvokeAsync(() => WriteTerminalLine(e.Line, e.StreamType), DispatcherPriority.Background);
        _runner.OutputReceived += (_, e) =>
            Dispatcher.InvokeAsync(() => WriteRunLine(e.Line, e.StreamType), DispatcherPriority.Background);
        // Fin du programme : après ses dernières lignes (même priorité), on remet l'invite du terminal
        _runner.ProcessExited += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            _runInTerminal = false;
            UpdateRunInputBar();
            UpdatePrompt();
        }, DispatcherPriority.Background);

        Closed += async (_, _) =>
        {
            await _runner.StopCurrentProcessAsync();
            await _terminal.StopSessionAsync();
        };

        _terminal.Shell = App.Settings.Current.TerminalShell;
        UpdateShellLabel();
        _ = StartTerminalAsync(HasWorkspace
            ? _workspacePath
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    private async Task StartTerminalAsync(string directory)
    {
        _terminalWriter.Clear();
        _terminalCwd = directory;
        UpdatePrompt();
        try
        {
            await _terminal.StartSessionAsync(directory);
        }
        catch (Exception ex)
        {
            _terminalWriter.WriteLine($"Impossible de démarrer le terminal : {ex.Message}", "ErrorBrush");
        }
    }

    // Le programme lancé avec Ctrl+F5 tourne dans le terminal (comme VS Code) : on y tape ses réponses
    private bool _runInTerminal;

    private bool IsCmdShell => string.Equals(_terminal.Shell, "cmd", StringComparison.OrdinalIgnoreCase);

    // Invite : « PS D:\dossier> » (PowerShell) ou « D:\dossier> » (cmd)
    private string ShellPrompt(string folder) => IsCmdShell ? $"{folder}>" : $"PS {folder}> ";

    // ----- Choix du shell : PowerShell ou Invite de commandes (cmd) -----

    private void UpdateShellLabel() => ShellName.Text = IsCmdShell ? "cmd" : "powershell";

    private void ShellButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ShellButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(ShellMenuItem("PowerShell", "powershell"));
        menu.Items.Add(ShellMenuItem("Invite de commandes (cmd)", "cmd"));
        menu.IsOpen = true;
    }

    private MenuItem ShellMenuItem(string header, string shell)
    {
        bool current = string.Equals(_terminal.Shell, shell, StringComparison.OrdinalIgnoreCase);
        var item = new MenuItem { Header = header, InputGestureText = current ? "✓" : "" };
        item.Click += async (_, _) => await SwitchShellAsync(shell);
        return item;
    }

    private async Task SwitchShellAsync(string shell)
    {
        if (string.Equals(_terminal.Shell, shell, StringComparison.OrdinalIgnoreCase)) return;

        string folder = _terminalCwd.Length > 0 ? _terminalCwd : _workspacePath;
        await _terminal.StopSessionAsync();
        _terminal.Shell = shell;
        UpdateShellLabel();
        ShowTerminal();
        await StartTerminalAsync(folder);
        await App.Settings.SaveAsync(App.Settings.Current with { TerminalShell = shell });
    }

    private void UpdatePrompt()
    {
        // Pendant qu'un programme tourne dans le terminal, pas d'invite : on écrit directement au programme
        TerminalPrompt.Text = _runInTerminal && _runner.IsRunning ? "" : ShellPrompt(_terminalCwd);
        TerminalPrompt.ToolTip = _terminalCwd;
    }

    // Une ligne venant de PowerShell : on la colore selon son type
    private void WriteTerminalLine(string line, OutputStreamType type)
    {
        // Lignes techniques (dossier courant) : jamais affichées
        if (line.Contains(CwdMarker, StringComparison.Ordinal))
        {
            if (line.StartsWith(CwdMarker, StringComparison.Ordinal))
            {
                _terminalCwd = line[CwdMarker.Length..];
                UpdatePrompt();
            }
            return;
        }

        if (type == OutputStreamType.StandardError)
        {
            _terminalWriter.WriteLine(line, "ErrorBrush");
            return;
        }

        if (type == OutputStreamType.SystemMessage)
        {
            _terminalWriter.WriteLine(line, "MutedText");
            return;
        }

        // « PS D:\dossier> commande » : invite normale, commande en couleur
        var prompt = PromptLine.Match(line);
        if (prompt.Success)
        {
            _terminalWriter.Write(ShellPrompt(prompt.Groups["path"].Value), "PrimaryText");
            _terminalWriter.WriteLine(prompt.Groups["command"].Value, "TerminalCommand");
            return;
        }

        _terminalWriter.WriteLine(line, "PrimaryText");
    }

    // Une ligne d'exécution (OUTPUT ou DEBUG CONSOLE)
    private void WriteRunLine(string line, OutputStreamType type)
    {
        string color = type switch
        {
            OutputStreamType.StandardError => "ErrorBrush",
            OutputStreamType.SystemMessage when line.Contains("Code de sortie : 0") => "SuccessBrush",
            OutputStreamType.SystemMessage when line.StartsWith("[Erreur]") || line.StartsWith("[Annulé]")
                => "ErrorBrush",
            OutputStreamType.SystemMessage => "MutedText",
            _ => "PrimaryText"
        };
        _runWriter.WriteLine(line, color);
    }

    // La zone de saisie n'apparaît que si un programme tourne et qu'on regarde Output ou Debug Console
    private void UpdateRunInputBar()
    {
        bool runTab = OutputContent.Visibility == Visibility.Visible ||
                      DebugConsoleContent.Visibility == Visibility.Visible;
        RunInputBar.Visibility = _runner.IsRunning && runTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RunInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        string text = RunInput.Text;
        RunInput.Clear();
        _runWriter.WriteLine(text, "TerminalCommand");   // on affiche ce qu'on a tapé, comme une console
        try
        {
            await _runner.SendInputAsync(text);
        }
        catch (Exception ex)
        {
            _runWriter.WriteLine(ex.Message, "ErrorBrush");
            UpdateRunInputBar();
        }
    }

    private async void TerminalInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab) _tabMatches = [];

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await SendTerminalCommandAsync(TerminalInput.Text);
                break;

            case Key.Up:
                e.Handled = true;
                BrowseHistory(-1);
                break;

            case Key.Down:
                e.Handled = true;
                BrowseHistory(+1);
                break;

            case Key.Tab:
                e.Handled = true;
                CompletePath();
                break;

            // Ctrl+C sans sélection : arrête la commande en cours (comme dans VS Code)
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control && TerminalInput.SelectionLength == 0:
                e.Handled = true;
                await InterruptTerminalAsync();
                break;

            // Ctrl+L : efface l'écran
            case Key.L when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                _terminalWriter.Clear();
                break;
        }
    }

    private async Task SendTerminalCommandAsync(string command)
    {
        TerminalInput.Clear();

        // Un programme (Ctrl+F5) attend une saisie : la ligne lui est envoyée
        if (_runInTerminal && _runner.IsRunning)
        {
            _terminalWriter.WriteLine(command, "TerminalCommand");
            try
            {
                await _runner.SendInputAsync(command);
            }
            catch (Exception ex)
            {
                _terminalWriter.WriteLine(ex.Message, "ErrorBrush");
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(command) &&
            (_history.Count == 0 || _history[^1] != command))
        {
            _history.Add(command);
        }
        _historyIndex = _history.Count;

        // cls / clear : on efface nous-mêmes l'écran
        if (command.Trim() is "cls" or "clear" or "Clear-Host")
        {
            _terminalWriter.Clear();
            return;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            _terminalWriter.WriteLine(ShellPrompt(_terminalCwd), "PrimaryText");
            return;
        }

        try
        {
            // cmd (lancé avec /Q) ne répète pas la commande : on l'affiche nous-mêmes, comme une console
            if (IsCmdShell)
            {
                _terminalWriter.Write(ShellPrompt(_terminalCwd), "PrimaryText");
                _terminalWriter.WriteLine(command, "TerminalCommand");
            }

            await _terminal.SendInputAsync(command);

            // Seulement après un changement de dossier : on demande discrètement le nouveau dossier.
            // (Jamais après un programme : la ligne technique serait lue par le programme comme une saisie.)
            if (ChangesDirectory(command))
            {
                await _terminal.SendInputAsync(IsCmdShell
                    ? $"echo {CwdMarker}%CD%"
                    : $"Write-Output ('{CwdMarker}' + (Get-Location).Path)");
            }
        }
        catch (Exception ex)
        {
            _terminalWriter.WriteLine(ex.Message, "ErrorBrush");
        }
    }

    private static bool ChangesDirectory(string command)
    {
        string first = command.TrimStart().Split(' ', 2)[0].ToLowerInvariant();
        if (first.StartsWith("cd.") || first.StartsWith("cd\\")) return true;   // cd.. et cd\
        return first is "cd" or "chdir" or "sl" or "set-location" or "pushd" or "popd" or "push-location" or "pop-location";
    }

    private void BrowseHistory(int direction)
    {
        if (_history.Count == 0) return;

        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        TerminalInput.Text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
        TerminalInput.CaretIndex = TerminalInput.Text.Length;
    }

    // Tab : complète le nom de fichier ou de dossier (Tab encore = suggestion suivante)
    private void CompletePath()
    {
        if (_tabMatches.Count == 0)
        {
            string text = TerminalInput.Text;
            int start = text.LastIndexOf(' ') + 1;
            _tabPrefix = text[..start];
            string partial = text[start..].Trim('"');

            string baseDir = _terminalCwd;
            string namePart = partial;
            int slash = partial.LastIndexOfAny(['\\', '/']);
            string folderPart = slash >= 0 ? partial[..(slash + 1)] : "";
            if (slash >= 0)
            {
                baseDir = Path.Combine(_terminalCwd, folderPart);
                namePart = partial[(slash + 1)..];
            }

            if (!Directory.Exists(baseDir)) return;
            try
            {
                _tabMatches = Directory.EnumerateFileSystemEntries(baseDir, namePart + "*")
                    .Select(entry => folderPart + Path.GetFileName(entry) +
                                     (Directory.Exists(entry) ? "\\" : ""))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            _tabIndex = 0;
            if (_tabMatches.Count == 0) return;
        }
        else
        {
            _tabIndex = (_tabIndex + 1) % _tabMatches.Count;
        }

        string match = _tabMatches[_tabIndex];
        if (match.Contains(' ')) match = $"\"{match}\"";
        TerminalInput.Text = _tabPrefix + match;
        TerminalInput.CaretIndex = TerminalInput.Text.Length;
    }

    // PowerShell ne reçoit pas Ctrl+C quand il est redirigé : on relance donc une session propre
    private async Task InterruptTerminalAsync()
    {
        // Ctrl+C pendant un programme lancé avec Ctrl+F5 : on arrête le programme, pas le terminal
        if (_runInTerminal && _runner.IsRunning)
        {
            _terminalWriter.WriteLine("^C", "ErrorBrush");
            await _runner.StopCurrentProcessAsync();
            return;
        }

        _terminalWriter.WriteLine("^C", "ErrorBrush");
        string directory = _terminalCwd;
        await _terminal.StopSessionAsync();
        _terminalCwd = directory;
        try
        {
            await _terminal.StartSessionAsync(directory);
        }
        catch (Exception ex)
        {
            _terminalWriter.WriteLine(ex.Message, "ErrorBrush");
        }
        UpdatePrompt();
    }

    // Cliquer dans le terminal place le curseur sur la ligne de commande (sauf si on sélectionne du texte)
    private void TerminalArea_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (TerminalOutput.Selection.IsEmpty) TerminalInput.Focus();
    }

    // ----- Panneau du bas : masquer, agrandir -----

    private GridLength _bottomPanelHeight = new(240);
    private bool _panelMaximized;

    private void ShowBottomPanel()
    {
        if (BottomPanel.Visibility == Visibility.Visible) return;
        BottomPanel.Visibility = Visibility.Visible;
        BottomPanelRow.MinHeight = 90;
        BottomPanelRow.Height = _bottomPanelHeight;
    }

    private void HideBottomPanel()
    {
        if (_panelMaximized) MaximizePanel_Click(this, new RoutedEventArgs());
        if (BottomPanelRow.ActualHeight > 0) _bottomPanelHeight = BottomPanelRow.Height;
        BottomPanel.Visibility = Visibility.Collapsed;
        BottomPanelRow.MinHeight = 0;
        BottomPanelRow.Height = new GridLength(0);
    }

    private void TogglePanel()
    {
        if (BottomPanel.Visibility == Visibility.Visible) HideBottomPanel();
        else ShowBottomPanel();
    }

    private void TogglePanel_Click(object sender, RoutedEventArgs e) => TogglePanel();

    private void HidePanel_Click(object sender, RoutedEventArgs e) => HideBottomPanel();

    private void MaximizePanel_Click(object sender, RoutedEventArgs e)
    {
        _panelMaximized = !_panelMaximized;
        if (_panelMaximized)
        {
            _bottomPanelHeight = BottomPanelRow.Height;
            EditorRow.MinHeight = 0;
            EditorRow.Height = new GridLength(0);
            BottomPanelRow.Height = new GridLength(1, GridUnitType.Star);
            MaximizePanelIcon.Text = "\uE73F";
            MaximizePanelButton.ToolTip = "Restaurer la taille du panneau";
        }
        else
        {
            EditorRow.MinHeight = 80;
            EditorRow.Height = new GridLength(1, GridUnitType.Star);
            BottomPanelRow.Height = _bottomPanelHeight;
            MaximizePanelIcon.Text = "\uE740";
            MaximizePanelButton.ToolTip = "Agrandir le panneau";
        }
    }

    // Efface le contenu de l'onglet affiché
    private void ClearPanel_Click(object sender, RoutedEventArgs e)
    {
        if (OutputContent.Visibility == Visibility.Visible) _outputWriter.Clear();
        else if (DebugConsoleContent.Visibility == Visibility.Visible) _debugWriter.Clear();
        else if (TerminalContent.Visibility == Visibility.Visible) _terminalWriter.Clear();
    }

    private void ShowBottomTab(Button tab)
    {
        ShowBottomPanel();
        BottomPanelTab_Click(tab, new RoutedEventArgs());
    }

    private void ShowTerminal()
    {
        ShowBottomTab(TerminalTab);
        TerminalInput.Focus();
    }

    private void ShowTerminal_Click(object sender, RoutedEventArgs e) => ShowTerminal();

    private void ClearTerminal_Click(object sender, RoutedEventArgs e) => _terminalWriter.Clear();

    private async void RestartTerminal_Click(object sender, RoutedEventArgs e)
    {
        await _terminal.StopSessionAsync();
        await StartTerminalAsync(_terminalCwd.Length > 0 ? _terminalCwd : _workspacePath);
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

        // Exécution (Ctrl+F5) : dans le TERMINAL, comme VS Code — on y voit la sortie et on y tape les réponses.
        // Débogage (F5) : dans DEBUG CONSOLE.
        _runInTerminal = !debug;
        _runWriter = debug ? _debugWriter : _terminalWriter;
        if (debug) _runWriter.Clear();
        ShowBottomTab(debug ? DebugConsoleTab : TerminalTab);
        StatusText.Text = debug ? $"Débogage de {Path.GetFileName(path)}..." : $"Exécution de {Path.GetFileName(path)}...";

        try
        {
            var request = new ExecutionRequest(path, Path.GetDirectoryName(path));
            var running = debug ? _runner.StartDebugAsync(request) : (Task)_runner.RunAsync(request);

            // Pendant l'exécution : on répond au programme dans le terminal (ou dans la barre de saisie en débogage)
            await Task.Delay(150);
            UpdateRunInputBar();
            UpdatePrompt();
            if (debug && RunInputBar.Visibility == Visibility.Visible) RunInput.Focus();
            else if (!debug) TerminalInput.Focus();

            await running;
            StatusText.Text = debug ? "Débogage démarré" : "Exécution terminée";
        }
        catch (Exception ex)
        {
            // Outil manquant (python, node...), fichier introuvable, etc.
            _runWriter.WriteLine($"[Erreur] {ex.Message}", "ErrorBrush");
            StatusText.Text = "Échec de l'exécution";
        }
        finally
        {
            UpdateRunInputBar();
            UpdatePrompt();
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
        InitializeAssistantLink();   // l'assistant voit le fichier ouvert et écrit dans l'éditeur (MainWindow.Assistant.cs)

        RegisterCommands();

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
            document.Editor.FontFamily = EditorFont(settings.FontFamily);
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
        if (IsSidebarVisible) ShowPanel(_activePanel);

        foreach (var document in _documents.Values)
        {
            ApplyEditorColors(document.Editor);
            document.Editor.SyntaxHighlighting = null;   // force le redessin avec les nouvelles couleurs
            ApplySyntaxHighlight(document.Editor, document.Path);
        }
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
        keys.Register(new CommandInfo("view.panel", "Afficher / masquer le panneau du bas", "Ctrl+J"),
            TogglePanel);
        keys.Register(new CommandInfo("view.sidebar", "Afficher / masquer la barre latérale", "Ctrl+B"),
            ToggleSidebar);
        keys.Register(new CommandInfo("file.close", "Fermer l'éditeur", "Ctrl+W"),
            CloseCurrentEditor);
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

    // Affiche un panneau de la barre latérale (Explorateur, Recherche, Git, Assistant)
    private void ShowPanel(string panelName)
    {
        var button = ActivityBar.Children.OfType<Button>()
            .FirstOrDefault(b => b.Tag as string == panelName);
        if (button is not null) SelectPanel(button);
    }

    // Clic sur une icône de la barre d'activité : comme VS Code, recliquer sur l'icône active masque la barre latérale
    private void ActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string panelName } button) return;

        // L'assistant s'ouvre à droite (comme le Chat de VS Code) : l'icône ouvre ou ferme le chat
        if (panelName == "Assistant")
        {
            SetChatVisible(!IsChatVisible);
            return;
        }

        if (panelName == _activePanel && IsSidebarVisible)
        {
            SetSidebarVisible(false);
            SetActivityIndicator(button, false);
            return;
        }

        SelectPanel(button);
    }

    private void SelectPanel(Button button)
    {
        if (button.Tag is not string panelName) return;

        if (panelName == "Assistant")
        {
            SetChatVisible(true);
            return;
        }

        SetSidebarVisible(true);
        ExplorerPanel.Visibility = panelName == "Explorer" ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = panelName == "Search" ? Visibility.Visible : Visibility.Collapsed;
        SourceControlPanel.Visibility = panelName == "Source Control" ? Visibility.Visible : Visibility.Collapsed;

        foreach (Button activityButton in ActivityBar.Children.OfType<Button>())
        {
            if (activityButton.Tag as string == "Assistant") continue;   // suit le chat, pas la barre latérale
            SetActivityIndicator(activityButton, ReferenceEquals(activityButton, button));
        }

        _activePanel = panelName;

        if (panelName == "Source Control")
        {
            _ = SourceControlPanel.RefreshCommandAsync();
        }
        else if (panelName == "Search")
        {
            SearchBox.Focus();
        }
    }

    // Icône active : blanche avec un trait à gauche ; les autres : grises
    private void SetActivityIndicator(Button button, bool active)
    {
        button.Style = (Style)FindResource(active ? "ActivityButtonActive" : "ActivityButton");
        button.ApplyTemplate();
        if (button.Template?.FindName("ActiveIndicator", button) is UIElement indicator)
            indicator.Opacity = active ? 1 : 0;
    }

    // ----- Barre latérale : afficher / masquer (Ctrl+B) -----

    private GridLength _sidebarWidth = new(280);

    private bool IsSidebarVisible => SidebarHost.Visibility == Visibility.Visible;

    private void SetSidebarVisible(bool visible)
    {
        if (visible == IsSidebarVisible) return;

        if (visible)
        {
            SidebarHost.Visibility = Visibility.Visible;
            SidebarColumn.MinWidth = 170;
            SidebarColumn.Width = _sidebarWidth;
            SplitterColumn.Width = new GridLength(4);
        }
        else
        {
            _sidebarWidth = SidebarColumn.Width;
            SidebarHost.Visibility = Visibility.Collapsed;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
        }
    }

    private void ToggleSidebar()
    {
        if (IsSidebarVisible)
        {
            SetSidebarVisible(false);
            foreach (Button activityButton in ActivityBar.Children.OfType<Button>())
            {
                if (activityButton.Tag as string != "Assistant") SetActivityIndicator(activityButton, false);
            }
        }
        else
        {
            ShowPanel(_activePanel);
        }
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    // ----- Chat de l'assistant IA, à droite -----

    private GridLength _chatWidth = new(380);

    private bool IsChatVisible => ChatHost.Visibility == Visibility.Visible;

    private void SetChatVisible(bool visible)
    {
        if (visible != IsChatVisible)
        {
            if (visible)
            {
                ChatHost.Visibility = Visibility.Visible;
                ChatColumn.MinWidth = 260;
                ChatColumn.Width = _chatWidth;
                ChatSplitterColumn.Width = new GridLength(4);
            }
            else
            {
                _chatWidth = ChatColumn.Width;
                ChatHost.Visibility = Visibility.Collapsed;
                ChatColumn.MinWidth = 0;
                ChatColumn.Width = new GridLength(0);
                ChatSplitterColumn.Width = new GridLength(0);
            }
        }

        var assistantButton = ActivityBar.Children.OfType<Button>().FirstOrDefault(b => b.Tag as string == "Assistant");
        if (assistantButton is not null) SetActivityIndicator(assistantButton, visible);

        if (visible)
        {
            AssistantView.RefreshContext();
            AssistantView.FocusInput();
        }
    }

    private void ChatStatus_Click(object sender, RoutedEventArgs e) => SetChatVisible(!IsChatVisible);

    private void LoadExplorer()
    {
        // Aucun dossier : on affiche « Aucun dossier ouvert » avec le bouton Ouvrir un dossier
        NoFolderPanel.Visibility = HasWorkspace ? Visibility.Collapsed : Visibility.Visible;
        ExplorerTree.Visibility = HasWorkspace ? Visibility.Visible : Visibility.Collapsed;
        ExplorerTree.Items.Clear();
        if (!HasWorkspace) return;

        // Le dossier ouvert est la première ligne, en majuscules (comme « CREATE-REACT-APP » dans VS Code)
        var root = CreateDirectoryNode(_workspacePath, isRoot: true);
        root.IsExpanded = true;
        root.ToolTip = _workspacePath;
        ExplorerTree.Items.Add(root);
        PopulateDirectoryNode(root);
    }

    private static DockPanel CreateExplorerHeader(string name, bool isDirectory, string? extension, bool isRoot = false)
    {
        var header = new DockPanel { LastChildFill = true, MinHeight = 20 };

        // Dossiers : seulement le chevron (dans le style de l'arbre) ; fichiers : icône colorée comme VS Code
        if (!isDirectory)
        {
            var (glyph, color) = GetFileIcon(extension);
            var icon = new TextBlock
            {
                Text = glyph,
                Width = 20,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.Bold,
                FontSize = glyph.Length > 2 ? 8 : 10,
                Foreground = color
            };
            DockPanel.SetDock(icon, Dock.Left);
            header.Children.Add(icon);
        }

        var label = new TextBlock
        {
            Text = isRoot ? name.ToUpperInvariant() : name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(isDirectory ? 2 : 0, 0, 0, 0)
        };
        if (isRoot)
        {
            label.FontWeight = FontWeights.Bold;
            label.FontSize = 11;
        }
        label.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryText");
        header.Children.Add(label);
        return header;
    }

    // Les pinceaux sont créés une seule fois (plus rapide quand le dossier contient beaucoup de fichiers)
    private static readonly Dictionary<string, Brush> IconBrushes = new();

    private static Brush IconBrush(byte r, byte g, byte b)
    {
        string key = $"{r},{g},{b}";
        if (!IconBrushes.TryGetValue(key, out Brush? brush))
        {
            brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            IconBrushes[key] = brush;
        }
        return brush;
    }

    private static (string Text, Brush Foreground) GetFileIcon(string? extension)
    {
        return extension switch
        {
            ".cs" => ("C#", IconBrush(103, 185, 107)),
            ".xaml" => ("X", IconBrush(86, 156, 214)),
            ".xml" or ".axml" or ".csproj" or ".props" or ".targets" => ("X", IconBrush(86, 156, 214)),
            ".js" or ".jsx" => ("JS", IconBrush(241, 196, 15)),
            ".ts" or ".tsx" => ("TS", IconBrush(49, 120, 198)),
            ".json" => ("{}", IconBrush(241, 196, 15)),
            ".css" or ".scss" or ".less" => ("#", IconBrush(86, 156, 214)),
            ".html" or ".htm" => ("<>", IconBrush(227, 76, 38)),
            ".py" => ("PY", IconBrush(53, 114, 165)),
            ".md" or ".markdown" => ("M", IconBrush(156, 220, 254)),
            ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" => ("C+", IconBrush(86, 156, 214)),
            ".go" => ("GO", IconBrush(78, 201, 176)),
            ".rs" => ("RS", IconBrush(206, 145, 120)),
            ".php" => ("PHP", IconBrush(136, 146, 191)),
            ".java" => ("JV", IconBrush(241, 101, 41)),
            ".rb" => ("RB", IconBrush(204, 52, 45)),
            ".sh" or ".bash" or ".ps1" or ".bat" or ".cmd" => ("$", IconBrush(137, 209, 133)),
            ".sql" => ("DB", IconBrush(206, 145, 120)),
            ".yml" or ".yaml" or ".toml" or ".ini" => ("Y", IconBrush(206, 145, 120)),
            _ => ("·", IconBrush(153, 153, 153))
        };
    }

    private static TreeViewItem CreateDirectoryNode(string path, bool isRoot = false)
    {
        string name = Path.GetFileName(path.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = path;
        var node = new TreeViewItem
        {
            Header = CreateExplorerHeader(name, isDirectory: true, extension: null, isRoot),
            Tag = path
        };

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

    private static readonly HashSet<string> HiddenFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", ".vs" };

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
                         .Where(d => !HiddenFolders.Contains(Path.GetFileName(d)))   // .git caché, comme VS Code
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

    // Un clic sur un fichier l'ouvre ; un clic sur un dossier l'ouvre ou le ferme (comme VS Code)
    private void ExplorerTree_ItemClicked(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? element = e.OriginalSource as DependencyObject;
        while (element is not null and not TreeViewItem)
        {
            if (element is ToggleButton) return;   // clic sur le chevron : déjà géré par l'arbre
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        if (element is not TreeViewItem { Tag: string path } item) return;

        if (File.Exists(path))
        {
            OpenDocument(path);
        }
        else if (Directory.Exists(path) && ExplorerTree.Items.IndexOf(item) < 0)
        {
            item.IsExpanded = !item.IsExpanded;   // le dossier racine reste toujours ouvert
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
            Title = "Ouvrir un dossier"
        };
        if (HasWorkspace) dialog.InitialDirectory = _workspacePath;

        if (dialog.ShowDialog(this) == true)
        {
            OpenWorkspace(dialog.FolderName);
        }
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (!HasWorkspace)
        {
            WelcomeNewFile_Click(sender, e);
            return;
        }

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
        if (!HasWorkspace)
        {
            StatusText.Text = "Ouvre d'abord un dossier.";
            return;
        }

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
        if (ExplorerTree.Items.Count > 0 && ExplorerTree.Items[0] is TreeViewItem root) root.IsExpanded = true;
        StatusText.Text = "Dossiers réduits.";
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

    private string? ShowNameDialog(string title, string prompt, string initialText = "", string confirmText = "Create")
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
            Foreground = (Brush)FindResource("PrimaryText"),
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
            Content = confirmText,
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

        input.Text = initialText;
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            // Pour renommer « index.html », on sélectionne « index » (comme dans VS Code)
            int dot = initialText.LastIndexOf('.');
            if (dot > 0) input.Select(0, dot);
            else input.SelectAll();
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

    // Dossiers ignorés par la recherche (très gros et inutiles) : la recherche reste rapide
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", "bin", "obj", "packages", "__pycache__", ".venv", "venv", "dist", "build"
    };

    private CancellationTokenSource? _searchCts;

    // La recherche tourne en arrière-plan : la fenêtre ne se fige plus
    private async void SearchFiles()
    {
        if (!HasWorkspace)
        {
            StatusText.Text = "Ouvre d'abord un dossier pour y chercher des fichiers.";
            return;
        }

        string query = SearchBox.Text.Trim();
        SearchResults.Items.Clear();
        if (query.Length == 0)
        {
            StatusText.Text = "Tape un nom de fichier à chercher.";
            return;
        }

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        string root = _workspacePath;
        StatusText.Text = "Recherche...";

        List<string> found;
        try
        {
            found = await Task.Run(() => FindFiles(root, query, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested) return;
        foreach (string file in found) SearchResults.Items.Add(file);
        StatusText.Text = found.Count == 0
            ? $"Aucun fichier ne contient « {query} »."
            : $"{found.Count} fichier(s) trouvé(s).";
    }

    private static List<string> FindFiles(string root, string query, CancellationToken token)
    {
        var results = new List<string>();
        var folders = new Stack<string>();
        folders.Push(root);

        while (folders.Count > 0 && results.Count < 2000)
        {
            token.ThrowIfCancellationRequested();
            string folder = folders.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    if (Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase))
                        results.Add(file);
                }

                foreach (string sub in Directory.EnumerateDirectories(folder))
                {
                    if (!IgnoredFolders.Contains(Path.GetFileName(sub))) folders.Push(sub);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // dossier illisible : on passe au suivant
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
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
            // Éditeur de code AvalonEdit (Membre 3) : numéros de ligne, coloration, annuler/rétablir
            var editor = new TextEditor
            {
                Text = content,
                // Police et taille choisies dans les Paramètres
                FontFamily = EditorFont(App.Settings.Current.FontFamily),
                FontSize = App.Settings.Current.FontSize,
                ShowLineNumbers = true,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 8, 4, 8),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            ApplyEditorColors(editor);
            ApplySyntaxHighlight(editor, fullPath);
            // Suggestions, extraits (Tab), fermeture automatique des parenthèses et des balises
            KeycesCode.Languages.CodeAssist.Attach(editor, fullPath);
            editor.ContextMenu = CreateEditorMenu(editor);   // clic droit : couper/copier/coller + assistant

            // En-tête de l'onglet comme VS Code : icône du langage, nom, bouton fermer (● si non enregistré)
            var (glyph, glyphColor) = GetFileIcon(Path.GetExtension(fullPath).ToLowerInvariant());
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.Bold,
                FontSize = glyph.Length > 2 ? 8 : 10,
                Foreground = glyphColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            var title = new TextBlock
            {
                Text = Path.GetFileName(fullPath),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 200
            };
            var closeButton = new Button { Style = (Style)FindResource("TabCloseButton") };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Height = 35 };
            header.Children.Add(icon);
            header.Children.Add(title);
            header.Children.Add(closeButton);

            // Fil d'Ariane sous les onglets : src › serviceWorker.js
            var page = new DockPanel();
            var breadcrumb = CreateBreadcrumb(fullPath, glyph, glyphColor);
            DockPanel.SetDock(breadcrumb, Dock.Top);
            page.Children.Add(breadcrumb);
            page.Children.Add(editor);

            var tab = new TabItem
            {
                Header = header,
                Content = page,
                Tag = fullPath,
                ToolTip = fullPath
            };
            // Clic avec la molette sur l'onglet : le ferme (comme VS Code)
            tab.MouseUp += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle && _documents.TryGetValue(fullPath, out var doc))
                    CloseDocument(doc);
            };
            var document = new EditorDocument(fullPath, tab, editor, title) { CloseButton = closeButton };
            closeButton.Click += (_, _) => CloseDocument(document);
            editor.TextArea.Caret.PositionChanged += (_, _) =>
            {
                // Seulement la position (rapide) : le reste change quand on change d'onglet
                var caret = editor.TextArea.Caret;
                CursorStatus.Text = $"Ln {caret.Line}, Col {caret.Column}";
            };
            editor.TextChanged += (_, _) =>
            {
                if (document.IsDirty) return;
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

    // Fil d'Ariane : chemin du fichier depuis le dossier ouvert
    private Border CreateBreadcrumb(string fullPath, string glyph, Brush glyphColor)
    {
        string relative = HasWorkspace && fullPath.StartsWith(_workspacePath, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(_workspacePath, fullPath)
            : Path.GetFileName(fullPath);
        string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) };
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            if (last)
            {
                line.Children.Add(new TextBlock
                {
                    Text = glyph,
                    FontWeight = FontWeights.Bold,
                    FontSize = glyph.Length > 2 ? 8 : 9,
                    Foreground = glyphColor,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0)
                });
            }

            var part = new TextBlock { Text = parts[i], VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
            part.SetResourceReference(TextBlock.ForegroundProperty, "BreadcrumbText");
            line.Children.Add(part);

            if (!last)
            {
                var separator = new TextBlock
                {
                    Text = "\uE76C",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 8,
                    Margin = new Thickness(6, 1, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                separator.SetResourceReference(TextBlock.ForegroundProperty, "BreadcrumbText");
                line.Children.Add(separator);
            }
        }

        var bar = new Border { Height = 22, Child = line };
        bar.SetResourceReference(Border.BackgroundProperty, "EditorBackground");
        return bar;
    }

    private EditorDocument? CurrentDocument =>
        EditorTabs.SelectedItem is TabItem { Tag: string path } && _documents.TryGetValue(path, out var document)
            ? document
            : null;

    // Barre d'état : Ln, Col, indentation, fin de ligne, langage du fichier affiché
    private void UpdateEditorStatus()
    {
        EditorDocument? document = CurrentDocument;
        if (document is null)
        {
            EditorStatus.Visibility = Visibility.Collapsed;
            return;
        }

        var caret = document.Editor.TextArea.Caret;
        EditorStatus.Visibility = Visibility.Visible;
        CursorStatus.Text = $"Ln {caret.Line}, Col {caret.Column}";
        IndentStatus.Text = document.Editor.Options.ConvertTabsToSpaces
            ? $"Spaces: {document.Editor.Options.IndentationSize}"
            : $"Tab Size: {document.Editor.Options.IndentationSize}";
        string start = document.Editor.Document.GetText(0, Math.Min(document.Editor.Document.TextLength, 20000));
        EolStatus.Text = start.Contains('\n') && !start.Contains("\r\n") ? "LF" : "CRLF";
        LanguageStatus.Text = DisplayLanguage(document.Path);
    }

    private static string DisplayLanguage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "C#",
        ".py" => "Python",
        ".js" or ".mjs" or ".cjs" => "JavaScript",
        ".jsx" => "JavaScript React",
        ".ts" => "TypeScript",
        ".tsx" => "TypeScript React",
        ".html" or ".htm" => "HTML",
        ".css" => "CSS",
        ".scss" => "SCSS",
        ".php" => "PHP",
        ".java" => "Java",
        ".c" => "C",
        ".h" or ".cpp" or ".cc" or ".hpp" => "C++",
        ".sql" => "SQL",
        ".json" => "JSON",
        ".xml" or ".csproj" or ".slnx" => "XML",
        ".xaml" => "XAML",
        ".md" => "Markdown",
        ".ps1" => "PowerShell",
        ".bat" or ".cmd" => "Batch",
        ".go" => "Go",
        ".rs" => "Rust",
        ".rb" => "Ruby",
        ".yml" or ".yaml" => "YAML",
        _ => "Plain Text"
    };

    // Titre comme VS Code : « fichier — dossier — Keyce'sCode »
    private void UpdateWindowTitle()
    {
        var parts = new List<string>();
        if (CurrentDocument is { } document)
            parts.Add(Path.GetFileName(document.Path) + (document.IsDirty ? " ●" : ""));
        if (HasWorkspace)
            parts.Add(Path.GetFileName(_workspacePath.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : _workspacePath);
        parts.Add("Keyce'sCode");
        Title = string.Join(" — ", parts);
    }

    private void CloseDocument(EditorDocument document)
    {
        _documents.Remove(document.Path);
        EditorTabs.Items.Remove(document.Tab);
        if (EditorTabs.Items.Count == 0)
        {
            WelcomePanel.Visibility = Visibility.Visible;
        }
        UpdateEditorStatus();
        UpdateWindowTitle();
    }

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, EditorTabs))
        {
            WelcomePanel.Visibility = EditorTabs.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateEditorStatus();
            UpdateWindowTitle();
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

    // Fichier modifié non enregistré : « ● » à la place de la croix, comme VS Code
    private void UpdateDocumentTitle(EditorDocument document)
    {
        document.Title.Text = Path.GetFileName(document.Path);
        if (document.CloseButton is not null) document.CloseButton.Tag = document.IsDirty ? "Dirty" : null;
        if (ReferenceEquals(document, CurrentDocument)) UpdateWindowTitle();
    }

    private void BottomPanelTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } selectedTab)
        {
            return;
        }

        ProblemsContent.Visibility = name == "Problems" ? Visibility.Visible : Visibility.Collapsed;
        OutputContent.Visibility = name == "Output" ? Visibility.Visible : Visibility.Collapsed;
        DebugConsoleContent.Visibility = name == "Debug Console" ? Visibility.Visible : Visibility.Collapsed;
        TerminalContent.Visibility = name == "Terminal" ? Visibility.Visible : Visibility.Collapsed;

        // Onglet actif : texte clair et souligné ; les autres : gris
        foreach (Button tab in BottomPanelTabs.Children.OfType<Button>())
        {
            bool active = ReferenceEquals(tab, selectedTab);
            tab.Style = (Style)FindResource(active ? "BottomPanelTabActive" : "BottomPanelTab");
            tab.ApplyTemplate();
            if (tab.Template?.FindName("TabUnderline", tab) is UIElement underline)
                underline.Opacity = active ? 1 : 0;
        }

        // Le nom du shell n'a de sens que pour le terminal
        ShellLabel.Visibility = name == "Terminal" ? Visibility.Visible : Visibility.Collapsed;
        UpdateRunInputBar();
        if (name == "Terminal") TerminalInput.Focus();
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

    // ----- Membre 3 : coloration syntaxique -----

    private static readonly KeycesCode.Languages.LanguageSyntaxHighlighter Highlighter = new();

    // Coloration d'Apostèl (Python, C#, JS/TS, HTML, CSS, PHP, C/C++), sinon celle intégrée à AvalonEdit
    private static void ApplySyntaxHighlight(TextEditor editor, string path)
    {
        bool dark = App.Themes.CurrentThemeId != "light";
        var definition = Highlighter.GetDefinition(path, dark) ?? FallbackHighlighting(path);
        if (definition is not null)
            KeycesCode.Languages.LanguageSyntaxHighlighter.ApplyTheme(definition, dark);
        editor.SyntaxHighlighting = definition;
    }

    // Police de l'éditeur avec des polices de secours si elle n'est pas installée sur le PC
    private static FontFamily EditorFont(string name) =>
        new($"{name}, Cascadia Mono, Consolas, Courier New");

    private static IHighlightingDefinition? FallbackHighlighting(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".xaml" or ".xml" or ".axml" or ".csproj" or ".props" or ".targets" or ".slnx" or ".config" or ".resx"
                => HighlightingManager.Instance.GetDefinition("XML"),
            _ => HighlightingManager.Instance.GetDefinitionByExtension(extension)
        };
    }

    // Couleurs de l'éditeur liées au thème (sombre / clair)
    private void ApplyEditorColors(TextEditor editor)
    {
        editor.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
        editor.SetResourceReference(Control.BackgroundProperty, "EditorBackground");
        editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "MutedText");
        editor.TextArea.SetResourceReference(
            ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "TextSelection");
        editor.TextArea.Caret.CaretBrush = (Brush)FindResource("PrimaryText");

        // Ligne du curseur entourée d'un fin cadre, comme VS Code
        editor.Options.HighlightCurrentLine = true;
        editor.TextArea.TextView.CurrentLineBackground = Brushes.Transparent;
        var border = new Pen((Brush)FindResource("CurrentLineBorder"), 2);
        border.Freeze();
        editor.TextArea.TextView.CurrentLineBorder = border;
    }

    // ----- Barre de titre : réduire, agrandir, fermer -----

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        // Fenêtre agrandie : Windows la fait déborder un peu de l'écran, on compense avec une marge
        RootBorder.BorderThickness = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(1);
        RootBorder.BorderBrush = maximized ? Brushes.Transparent : (Brush)FindResource("BorderBrush");
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restaurer" : "Agrandir";
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "Keyce'sCode 1.0.0\n\nÉditeur de code du groupe Keyce (B2) : éditeur avec coloration et complétion, " +
            "terminal, exécution, Git/GitHub et assistant IA Google Gemini.",
            "À propos de Keyce'sCode", MessageBoxButton.OK, MessageBoxImage.Information);

    private void SaveAll_Click(object sender, RoutedEventArgs e) => SaveAllDocuments();

    private void CloseEditor_Click(object sender, RoutedEventArgs e) => CloseCurrentEditor();

    private void CloseCurrentEditor()
    {
        if (CurrentDocument is { } document) CloseDocument(document);
    }

    private void BranchStatus_Click(object sender, RoutedEventArgs e) => ShowPanel("Source Control");

    private void ProblemsStatus_Click(object sender, RoutedEventArgs e) => ShowBottomTab(ProblemsTab);

    private sealed class EditorDocument
    {
        public EditorDocument(string path, TabItem tab, TextEditor editor, TextBlock title)
        {
            Path = path;
            Tab = tab;
            Editor = editor;
            Title = title;
        }

        public string Path { get; }
        public TabItem Tab { get; }
        public TextEditor Editor { get; }
        public TextBlock Title { get; }
        public Button? CloseButton { get; init; }
        public bool IsDirty { get; set; }
    }
}
