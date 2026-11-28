using System.Windows;
using System.Windows.Threading;
using KeycesCode.Assistant;
using KeycesCode.Settings;
using KeycesCode.Settings.KeyBindings;

namespace KeycesCode.App;

public partial class App : Application
{
    // Services partagés par toute l'application (Membre 5)
    public static SettingsService Settings { get; } = new();
    public static ThemeService Themes { get; } = new();
    public static KeyBindingService KeyBindings { get; } = new(Settings);
    public static GeminiAssistantService Assistant { get; } = new(() => Settings.Current.GeminiApiKey);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Toute erreur non prévue s'affiche au lieu de fermer l'application en silence
        DispatcherUnhandledException += OnUnhandledException;

        // Quand le thème change, les couleurs de l'interface (Membre 1) changent aussi
        Themes.ThemeChanged += (_, themeId) => ApplyInterfaceColors(themeId);

        try
        {
            await Settings.LoadAsync();

            var theme = Themes.AvailableThemes.Contains(Settings.Current.ThemeId)
                ? Settings.Current.ThemeId
                : "dark";
            Themes.Apply(theme);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Keyce'sCode n'a pas pu démarrer",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // Remplace Colors.xaml par Colors.Light.xaml (ou l'inverse) dans les ressources de l'application
    private void ApplyInterfaceColors(string themeId)
    {
        var file = themeId == "light" ? "Colors.Light.xaml" : "Colors.xaml";
        var next = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/KeycesCode.App;component/Themes/{file}")
        };

        var dictionaries = Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Themes/Colors", StringComparison.OrdinalIgnoreCase) == true);

        if (current is null) dictionaries.Insert(0, next);
        else dictionaries[dictionaries.IndexOf(current)] = next;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "Erreur inattendue",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;   // l'application continue au lieu de se fermer
    }
}
