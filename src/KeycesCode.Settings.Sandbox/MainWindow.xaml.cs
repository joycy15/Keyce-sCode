using System.Windows;
using System.Windows.Input;
using KeycesCode.Core.Models;
using KeycesCode.Settings.KeyBindings;

namespace KeycesCode.Settings.Sandbox;

public partial class MainWindow : Window
{
    private GridLength _sideBarWidth = new(320);

    public MainWindow()
    {
        InitializeComponent();
        RegisterCommands();

        SettingsEditor.Initialize(App.Settings, App.Themes);
        SettingsEditor.InitializeShortcuts(App.KeyBindings);

        // L'assistant lit le modèle choisi dans les Paramètres
        AssistantView.Initialize(App.Assistant, () => App.Settings.Current.AssistantModel);
        App.Settings.SettingsChanged += (_, _) => _ = AssistantView.CheckStatusAsync();

        App.Themes.ThemeChanged += (_, _) => UpdateThemeStatus();
        UpdateThemeStatus();
    }

    // Toutes les commandes du bac à sable, avec leur raccourci par défaut
    private void RegisterCommands()
    {
        var keys = App.KeyBindings;

        keys.Register(new CommandInfo("view.settings", "Ouvrir les paramètres", "Ctrl+,"),
            () => SettingsTab.IsChecked = true);

        keys.Register(new CommandInfo("view.git", "Afficher le contrôle de code source", "Ctrl+Shift+G"),
            () => GitTab.IsChecked = true);

        keys.Register(new CommandInfo("view.assistant", "Afficher l'assistant IA", "Ctrl+Shift+I"),
            () => AssistantTab.IsChecked = true);

        keys.Register(new CommandInfo("git.refresh", "Git : actualiser", "F5"),
            async () => await GitView.RefreshCommandAsync());

        keys.Register(new CommandInfo("git.pull", "Git : pull", "Ctrl+Alt+L"),
            async () => await GitView.PullCommandAsync());

        keys.Register(new CommandInfo("git.push", "Git : push", "Ctrl+Alt+P"),
            async () => await GitView.PushCommandAsync());

        keys.Register(new CommandInfo("theme.toggle", "Basculer entre thème sombre et clair", "Ctrl+Alt+T"),
            async () => await ToggleThemeAsync());
    }

    // Chaque touche pressée dans la fenêtre : est-ce un raccourci connu ?
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SettingsEditor.IsCapturingShortcut) return;   // la page Paramètres capture la touche

        var shortcut = KeyReader.Read(e);
        if (shortcut is not null && App.KeyBindings.TryExecute(shortcut))
            e.Handled = true;
    }

    private async Task ToggleThemeAsync()
    {
        var next = App.Themes.CurrentThemeId == "dark" ? "light" : "dark";
        App.Themes.Apply(next);
        await App.Settings.SaveAsync(App.Settings.Current with { ThemeId = next });
        SettingsEditor.Reload();   // met à jour les boutons Sombre / Clair de la page
    }

    private void Activity_Checked(object sender, RoutedEventArgs e)
    {
        // Pendant InitializeComponent, certains éléments n'existent pas encore
        if (SettingsTab is null || SettingsEditor is null || SideBar is null
            || GitView is null || AssistantView is null) return;

        var showSettings = SettingsTab.IsChecked == true;

        if (showSettings)
        {
            if (SideBarColumn.Width.Value > 0) _sideBarWidth = SideBarColumn.Width;
            SideBarColumn.MinWidth = 0;
            SideBarColumn.Width = new GridLength(0);
        }
        else
        {
            SideBarColumn.MinWidth = 200;
            SideBarColumn.Width = _sideBarWidth;
        }

        SideBar.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;
        Splitter.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsEditor.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;

        // Dans la barre latérale : Git OU l'assistant
        GitView.Visibility = GitTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AssistantView.Visibility = AssistantTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GitView_BranchChanged(object? sender, string branch) =>
        BranchStatus.Text = string.IsNullOrEmpty(branch) ? "" : $"⎇ {branch}";

    private void UpdateThemeStatus() =>
        ThemeStatus.Text = App.Themes.CurrentThemeId == "dark" ? "Sombre" : "Clair";
}
