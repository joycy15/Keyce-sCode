namespace KeycesCode.Core.Models;

// Tous les paramètres de l'utilisateur, avec leurs valeurs par défaut.
public sealed record AppSettings
{
    public string ThemeId { get; init; } = "dark";
    public string FontFamily { get; init; } = "Cascadia Code";
    public double FontSize { get; init; } = 14;
    public bool AutoSave { get; init; } = false;
    public int AutoSaveDelaySeconds { get; init; } = 30;
    public bool ShowMinimap { get; init; } = true;

    // Terminal : « powershell » ou « cmd » (Invite de commandes)
    public string TerminalShell { get; init; } = "powershell";
    // Assistant IA (Google Gemini) : modèle et clé API (gratuite sur aistudio.google.com/apikey)
    public string AssistantModel { get; init; } = "gemini-flash-latest";
    public string GeminiApiKey { get; init; } = "";

    // Derniers dossiers ouverts (le plus récent en premier), affichés sur la page d'accueil
    public IReadOnlyList<string> RecentFolders { get; init; } = [];

    // Raccourcis modifiés par l'utilisateur : identifiant de commande → raccourci
    // (les commandes absentes gardent leur raccourci par défaut)
    public IReadOnlyDictionary<string, string> KeyBindings { get; init; } = new Dictionary<string, string>();
}
