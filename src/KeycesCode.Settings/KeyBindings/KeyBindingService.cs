using KeycesCode.Core.Abstractions;
using KeycesCode.Core.Models;

namespace KeycesCode.Settings.KeyBindings;

public sealed class KeyBindingService : IKeyBindingService
{
    private readonly ISettingsService _settings;
    private readonly List<CommandInfo> _commands = new();
    private readonly Dictionary<string, Action> _actions = new();

    public KeyBindingService(ISettingsService settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<CommandInfo> Commands => _commands;

    public event EventHandler? ShortcutsChanged;

    public void Register(CommandInfo command, Action execute)
    {
        if (_actions.ContainsKey(command.Id))
            throw new InvalidOperationException($"La commande {command.Id} est déjà enregistrée.");

        _commands.Add(command);
        _actions[command.Id] = execute;
    }

    public string GetShortcut(string commandId)
    {
        // 1. Le raccourci choisi par l'utilisateur, s'il existe
        if (_settings.Current.KeyBindings.TryGetValue(commandId, out var custom))
            return custom;

        // 2. Sinon, le raccourci par défaut de la commande
        return _commands.FirstOrDefault(c => c.Id == commandId)?.DefaultShortcut ?? "";
    }

    public bool IsCustomized(string commandId) =>
        _settings.Current.KeyBindings.ContainsKey(commandId);

    public async Task SetShortcutAsync(string commandId, string shortcut)
    {
        // Refuse un raccourci déjà utilisé par une AUTRE commande
        var owner = FindCommand(shortcut);
        if (owner is not null && owner.Id != commandId)
            throw new InvalidOperationException($"{shortcut} est déjà utilisé par « {owner.Title} ».");

        // On copie la table actuelle et on modifie la copie (les paramètres sont immuables)
        var bindings = new Dictionary<string, string>(_settings.Current.KeyBindings)
        {
            [commandId] = shortcut
        };
        await _settings.SaveAsync(_settings.Current with { KeyBindings = bindings });

        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResetAsync(string commandId)
    {
        var bindings = new Dictionary<string, string>(_settings.Current.KeyBindings);
        if (!bindings.Remove(commandId)) return;   // rien à réinitialiser

        await _settings.SaveAsync(_settings.Current with { KeyBindings = bindings });
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool TryExecute(string shortcut)
    {
        var command = FindCommand(shortcut);
        if (command is null) return false;

        _actions[command.Id]();   // lance l'action de la commande
        return true;
    }

    // Quelle commande utilise ce raccourci ? (null si aucune)
    private CommandInfo? FindCommand(string shortcut) =>
        _commands.FirstOrDefault(c =>
            string.Equals(GetShortcut(c.Id), shortcut, StringComparison.OrdinalIgnoreCase));
}
