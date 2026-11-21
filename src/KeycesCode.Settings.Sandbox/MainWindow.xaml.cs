using System.Windows;
using System.Windows.Input;

namespace KeycesCode.Settings.Sandbox;

public partial class MainWindow : Window
{
    private GridLength _sideBarWidth = new(320);

    public MainWindow()
    {
        InitializeComponent();

        // On donne à la page les services de l'application
        SettingsEditor.Initialize(App.Settings, App.Themes);

        // La barre de statut suit le thème, quel que soit l'endroit où il est changé
        App.Themes.ThemeChanged += (_, _) => UpdateThemeStatus();
        UpdateThemeStatus();
    }

    // Git : barre latérale visible. Paramètres : la page prend toute la largeur.
    private void Activity_Checked(object sender, RoutedEventArgs e)
    {
        if (SettingsTab is null || SettingsEditor is null || SideBar is null) return;

        var showSettings = SettingsTab.IsChecked == true;

        if (showSettings)
        {
            if (SideBarColumn.Width.Value > 0) _sideBarWidth = SideBarColumn.Width;  // retient la largeur
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
    }

    // Ctrl+, ouvre les paramètres, comme dans VS Code
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SettingsTab.IsChecked = true;
            e.Handled = true;
        }
    }

    private void GitView_BranchChanged(object? sender, string branch) =>
        BranchStatus.Text = string.IsNullOrEmpty(branch) ? "" : $"⎇ {branch}";

    private void UpdateThemeStatus() =>
        ThemeStatus.Text = App.Themes.CurrentThemeId == "dark" ? "Sombre" : "Clair";
}
