using System.Windows;
using KeycesCode.Core.Abstractions;

namespace KeycesCode.Settings;

public sealed class ThemeService : IThemeService
{
    // Identifiant du thème → emplacement de son fichier XAML
    private static readonly Dictionary<string, string> Themes = new()
    {
        ["dark"] = "Themes/DarkTheme.xaml",
        ["light"] = "Themes/LightTheme.xaml",
    };

    private ResourceDictionary? _currentDictionary;

    public IReadOnlyList<string> AvailableThemes => Themes.Keys.ToList();

    public string CurrentThemeId { get; private set; } = "dark";

    public event EventHandler<string>? ThemeChanged;

    public void Apply(string themeId)
    {
        if (!Themes.TryGetValue(themeId, out var path))
            throw new ArgumentException($"Thème inconnu : {themeId}", nameof(themeId));

        // Charge le fichier XAML du thème depuis la DLL KeycesCode.Settings
        var newDictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/KeycesCode.Settings;component/{path}",
                             UriKind.Absolute)
        };

        // Remplace l'ancien thème par le nouveau dans les ressources de l'application
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (_currentDictionary is not null)
            dictionaries.Remove(_currentDictionary);
        dictionaries.Add(newDictionary);

        _currentDictionary = newDictionary;
        CurrentThemeId = themeId;
        ThemeChanged?.Invoke(this, themeId);
    }
}