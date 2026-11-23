using System.Windows.Input;

namespace KeycesCode.Settings.KeyBindings;

// Le seul fichier qui dépend de WPF : il transforme une touche pressée en texte "Ctrl+S"
public static class KeyReader
{
    public static string? Read(KeyEventArgs e)
    {
        // Avec Alt, WPF met la vraie touche dans SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Appuyer seulement sur Ctrl, Maj ou Alt ne forme pas encore un raccourci
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return null;

        var modifiers = Keyboard.Modifiers;
        return ShortcutText.Create(
            modifiers.HasFlag(ModifierKeys.Control),
            modifiers.HasFlag(ModifierKeys.Shift),
            modifiers.HasFlag(ModifierKeys.Alt),
            KeyName(key));
    }

    // Noms lisibles pour quelques touches spéciales
    private static string KeyName(Key key) => key switch
    {
        Key.OemComma  => ",",
        Key.OemPeriod => ".",
        Key.OemMinus  => "-",
        Key.OemPlus   => "+",
        Key.Return    => "Entrée",
        Key.Space     => "Espace",
        Key.Back      => "Retour",
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString(),
    };
}
