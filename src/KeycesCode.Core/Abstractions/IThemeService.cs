namespace KeycesCode.Core.Abstractions;

public interface IThemeService
{
    IReadOnlyList<string> AvailableThemes { get; }   // "dark", "light"
    string CurrentThemeId { get; }
    void Apply(string themeId);                      // change le thème immédiatement
    event EventHandler<string>? ThemeChanged;        // prévient les autres modules
}