using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeycesCode.Core.Abstractions;
using KeycesCode.Core.Models;

namespace KeycesCode.Settings;

public partial class SettingsPage : UserControl
{
    private ISettingsService? _settings;
    private IThemeService? _themes;
    private bool _loading;   // vrai pendant qu'on remplit les champs : on n'enregistre rien

    public SettingsPage()
    {
        InitializeComponent();
    }

    // Les services sont donnés par l'application (injection de dépendances)
    public void Initialize(ISettingsService settings, IThemeService themes)
    {
        _settings = settings;
        _themes = themes;
        LoadValues();
    }

    // Remplit les champs avec les paramètres actuels
    private void LoadValues()
    {
        if (_settings is null) return;

        _loading = true;
        var s = _settings.Current;
        DarkThemeOption.IsChecked = s.ThemeId == "dark";
        LightThemeOption.IsChecked = s.ThemeId == "light";
        FontFamilyInput.Text = s.FontFamily;
        FontSizeInput.Text = s.FontSize.ToString(CultureInfo.CurrentCulture);
        MinimapCheck.IsChecked = s.ShowMinimap;
        AutoSaveCheck.IsChecked = s.AutoSave;
        AutoSaveDelayInput.Text = s.AutoSaveDelaySeconds.ToString(CultureInfo.CurrentCulture);
        AssistantModelInput.Text = s.AssistantModel;
        _loading = false;
    }

    // Enregistre une modification : "change" reçoit les paramètres actuels et renvoie les nouveaux
    private async Task SaveAsync(Func<AppSettings, AppSettings> change)
    {
        if (_settings is null || _loading) return;

        await _settings.SaveAsync(change(_settings.Current));
        ShowStatus("Paramètre enregistré.");
    }

    // ========== Chaque réglage ==========

    private async void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _themes is null || sender is not RadioButton { Tag: string themeId }) return;

        _themes.Apply(themeId);
        await SaveAsync(s => s with { ThemeId = themeId });
    }

    private async void FontFamily_LostFocus(object sender, RoutedEventArgs e)
    {
        var value = FontFamilyInput.Text.Trim();
        if (value.Length == 0)
        {
            ShowError("Le nom de la police ne peut pas être vide.");
            return;
        }
        await SaveAsync(s => s with { FontFamily = value });
    }

    private async void FontSize_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(FontSizeInput.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var size)
            || size < 6 || size > 72)
        {
            ShowError("La taille de police doit être un nombre entre 6 et 72.");
            return;
        }
        await SaveAsync(s => s with { FontSize = size });
    }

    private async void Minimap_Click(object sender, RoutedEventArgs e) =>
        await SaveAsync(s => s with { ShowMinimap = MinimapCheck.IsChecked == true });

    private async void AutoSave_Click(object sender, RoutedEventArgs e) =>
        await SaveAsync(s => s with { AutoSave = AutoSaveCheck.IsChecked == true });

    private async void AutoSaveDelay_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AutoSaveDelayInput.Text, out var seconds) || seconds < 1 || seconds > 600)
        {
            ShowError("Le délai doit être un nombre entier entre 1 et 600.");
            return;
        }
        await SaveAsync(s => s with { AutoSaveDelaySeconds = seconds });
    }

    private async void AssistantModel_LostFocus(object sender, RoutedEventArgs e)
    {
        var value = AssistantModelInput.Text.Trim();
        if (value.Length == 0)
        {
            ShowError("Le nom du modèle ne peut pas être vide.");
            return;
        }
        await SaveAsync(s => s with { AssistantModel = value });
    }

    // Entrée dans un champ = valider (on passe au champ suivant, ce qui déclenche LostFocus)
    private void Field_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            e.Handled = true;
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    // ========== Recherche et navigation ==========

    // Filtre les réglages en ignorant majuscules ET accents ("theme" trouve "Thème")
    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchInput.Text.Trim();
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        var options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

        foreach (var section in SettingsList.Children.OfType<StackPanel>())
        {
            var anyVisible = false;

            foreach (var item in section.Children.OfType<Border>())
            {
                var keywords = item.Tag as string ?? "";
                var match = query.Length == 0 || compare.IndexOf(keywords, query, options) >= 0;

                item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                anyVisible |= match;
            }

            // On cache aussi le titre de la catégorie si aucun de ses réglages ne correspond
            section.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // Clic dans la table des matières : fait défiler jusqu'à la catégorie
    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sectionName } && FindName(sectionName) is FrameworkElement section)
            section.BringIntoView();
    }

    private void ShowStatus(string message) => StatusText.Text = message;

    private void ShowError(string message)
    {
        ShowStatus(message);
        LoadValues();   // remet l'ancienne valeur dans le champ
    }
}
