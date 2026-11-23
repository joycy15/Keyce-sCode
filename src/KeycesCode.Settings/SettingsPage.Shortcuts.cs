using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeycesCode.Core.Abstractions;
using KeycesCode.Settings.KeyBindings;

namespace KeycesCode.Settings;

// Deuxième morceau de la classe SettingsPage : la section « Raccourcis clavier »
public partial class SettingsPage
{
    private IKeyBindingService? _keyBindings;
    private string? _capturingCommandId;   // commande en cours de modification (null = aucune)

    // La fenêtre l'utilise pour ne pas déclencher de raccourci pendant une capture
    public bool IsCapturingShortcut => _capturingCommandId is not null;

    public void InitializeShortcuts(IKeyBindingService keyBindings)
    {
        _keyBindings = keyBindings;
        _keyBindings.ShortcutsChanged += (_, _) => RefreshShortcuts();
        RefreshShortcuts();
    }

    // Relit les paramètres (par exemple après un changement de thème au clavier)
    public void Reload() => LoadValues();

    private void RefreshShortcuts()
    {
        if (_keyBindings is null) return;

        ShortcutsList.ItemsSource = _keyBindings.Commands
            .Select(c => new ShortcutRow(
                c.Id,
                c.Title,
                c.Id == _capturingCommandId ? "Appuie sur les touches…" : _keyBindings.GetShortcut(c.Id),
                _keyBindings.IsCustomized(c.Id)))
            .ToList();
    }

    private void EditShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string commandId }) return;

        _capturingCommandId = commandId;
        RefreshShortcuts();
        ShowStatus("Appuie sur la nouvelle combinaison de touches (Échap pour annuler).");
        Focus();
    }

    private async void ResetShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_keyBindings is null || sender is not Button { Tag: string commandId }) return;

        await _keyBindings.ResetAsync(commandId);
        ShowStatus("Raccourci réinitialisé.");
    }

    // Pendant une capture : la prochaine combinaison devient le nouveau raccourci
    private async void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingCommandId is null || _keyBindings is null) return;

        e.Handled = true;   // la touche ne fait rien d'autre pendant la capture

        if (e.Key == Key.Escape)
        {
            StopCapture("Modification annulée.");
            return;
        }

        var shortcut = KeyReader.Read(e);
        if (shortcut is null) return;   // seulement Ctrl ou une lettre seule : on attend la suite

        try
        {
            await _keyBindings.SetShortcutAsync(_capturingCommandId, shortcut);
            StopCapture($"Raccourci enregistré : {shortcut}");
        }
        catch (InvalidOperationException ex)
        {
            StopCapture(ex.Message);   // ex. : « Ctrl+, est déjà utilisé par … »
        }
    }

    private void StopCapture(string message)
    {
        _capturingCommandId = null;
        RefreshShortcuts();
        ShowStatus(message);
    }
}

// Une ligne de la liste des raccourcis
public sealed record ShortcutRow(string CommandId, string Title, string Shortcut, bool IsCustom);
