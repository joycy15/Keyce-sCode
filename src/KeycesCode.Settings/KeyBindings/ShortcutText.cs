namespace KeycesCode.Settings.KeyBindings;

public static class ShortcutText
{
    // (Ctrl, Maj, Alt, "G") → "Ctrl+Shift+G". Renvoie null si ce n'est pas un raccourci valable.
    public static string? Create(bool ctrl, bool shift, bool alt, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        // F1 à F24 peuvent servir seules ; une simple lettre non (sinon on ne pourrait plus écrire)
        var isFunctionKey = key.Length >= 2 && key[0] == 'F' && int.TryParse(key[1..], out _);
        if (!ctrl && !alt && !isFunctionKey) return null;

        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        parts.Add(key);

        return string.Join("+", parts);
    }
}
