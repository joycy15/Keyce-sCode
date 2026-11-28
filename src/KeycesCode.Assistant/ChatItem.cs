using System.ComponentModel;
using System.Text.RegularExpressions;

namespace KeycesCode.Assistant;

// Un message affiché dans le panneau.
// INotifyPropertyChanged : quand Text change (mot par mot), l'écran se met à jour.
public sealed class ChatItem : INotifyPropertyChanged
{
    private string _text;
    private bool _isComplete;

    public ChatItem(bool isUser, string text)
    {
        IsUser = isUser;
        _text = text;
        if (isUser) Complete();
    }

    public bool IsUser { get; }
    public string Author => IsUser ? "Toi" : "Assistant";

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            OnChanged(nameof(Text));
        }
    }

    // Faux pendant que la réponse s'écrit ; vrai à la fin : le message est alors découpé
    // en morceaux de texte et blocs de code (avec les boutons Insérer, Remplacer, Copier)
    public bool IsComplete
    {
        get => _isComplete;
        private set
        {
            _isComplete = value;
            OnChanged(nameof(IsComplete));
        }
    }

    public IReadOnlyList<ChatSegment> Segments { get; private set; } = [];

    public void Complete()
    {
        Segments = ChatSegment.Parse(Text);
        OnChanged(nameof(Segments));
        IsComplete = true;
    }

    // Ajoute une petite ligne à la fin du message (ex. « ✏️ Écrit dans main.py »)
    public void AddNote(string note)
    {
        Segments = [.. Segments, new ChatSegment(note, false, "")];
        OnChanged(nameof(Segments));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

// Un morceau de message : du texte, ou un bloc de code ```langage ... ```
// Un bloc peut porter une consigne pour l'éditeur après le langage :
//   ```python:replace-file        → remplacer tout le fichier ouvert
//   ```python:replace-selection   → remplacer la sélection
//   ```python:insert              → insérer à la position du curseur
//   ```python:file=src/jeu.py     → écrire tout le fichier src/jeu.py du projet (ouvert ou non, créé s'il n'existe pas)
//   ```python:new=jeu.py          → pareil (ancien nom)
public sealed record ChatSegment(string Text, bool IsCode, string Language,
                                 CodeActionKind? Action = null, string FileName = "")
{
    private static readonly Regex CodeBlock =
        new(@"```([\w#+.-]*)(?::([^\s`]+))?[^\S\n]*\n?(.*?)```", RegexOptions.Singleline);

    // Texte affiché au-dessus du bloc de code : langage + ce que l'assistant en fait
    public string Header => Action switch
    {
        CodeActionKind.ReplaceFile => $"{Language} · remplace le fichier",
        CodeActionKind.ReplaceSelection => $"{Language} · remplace la sélection",
        CodeActionKind.InsertAtCursor => $"{Language} · inséré au curseur",
        CodeActionKind.NewFile => $"{Language} · écrit dans {FileName}",
        _ => Language
    };

    public static IReadOnlyList<ChatSegment> Parse(string message)
    {
        var segments = new List<ChatSegment>();
        int position = 0;

        foreach (Match match in CodeBlock.Matches(message))
        {
            AddText(segments, message[position..match.Index]);
            string language = match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : "code";
            (CodeActionKind? action, string fileName) = ParseAction(match.Groups[2].Value);
            segments.Add(new ChatSegment(match.Groups[3].Value.TrimEnd('\r', '\n'), true, language,
                                         action, fileName));
            position = match.Index + match.Length;
        }

        AddText(segments, message[position..]);
        return segments;
    }

    // « replace-file », « insert », « new=jeu.py »... → consigne pour l'éditeur
    public static (CodeActionKind? Action, string FileName) ParseAction(string marker)
    {
        marker = marker.Trim();
        foreach (string prefix in new[] { "file=", "new=", "path=" })
        {
            if (!marker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string name = marker[prefix.Length..].Trim().Trim('"', '\'');
            return name.Length > 0 ? (CodeActionKind.NewFile, name) : (null, "");
        }

        return marker.ToLowerInvariant() switch
        {
            "replace-file" or "file" => (CodeActionKind.ReplaceFile, ""),
            "replace-selection" or "selection" => (CodeActionKind.ReplaceSelection, ""),
            "insert" => (CodeActionKind.InsertAtCursor, ""),
            _ => (null, "")
        };
    }

    private static void AddText(List<ChatSegment> segments, string text)
    {
        text = text.Trim('\r', '\n');
        if (text.Length > 0) segments.Add(new ChatSegment(text, false, ""));
    }
}
