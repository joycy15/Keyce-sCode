using System.Windows;

namespace KeycesCode.Settings.Sandbox;

public partial class App : Application
{
    public static SettingsService Settings { get; } = new();
    public static ThemeService Themes { get; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        await Settings.LoadAsync();

        var theme = Themes.AvailableThemes.Contains(Settings.Current.ThemeId)
            ? Settings.Current.ThemeId
            : "dark";
        Themes.Apply(theme);

        new MainWindow().Show();
    }
}