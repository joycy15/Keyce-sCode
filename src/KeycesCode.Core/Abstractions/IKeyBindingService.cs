using KeycesCode.Core.Models;

namespace KeycesCode.Core.Abstractions;

public interface IKeyBindingService
{
    IReadOnlyList<CommandInfo> Commands { get; }

    void Register(CommandInfo command, Action execute);   // déclarer une commande et son action
    string GetShortcut(string commandId);                 // raccourci personnalisé ou par défaut
    bool IsCustomized(string commandId);                  // l'utilisateur l'a-t-il changé ?
    Task SetShortcutAsync(string commandId, string shortcut);
    Task ResetAsync(string commandId);                    // revenir au raccourci par défaut
    bool TryExecute(string shortcut);                     // appelé à chaque touche pressée

    event EventHandler? ShortcutsChanged;
}
