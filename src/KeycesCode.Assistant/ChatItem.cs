using System.ComponentModel;

namespace KeycesCode.Assistant;

// Un message affiché dans le panneau.
// INotifyPropertyChanged : quand Text change (mot par mot), l'écran se met à jour.
public sealed class ChatItem : INotifyPropertyChanged
{
    private string _text;

    public ChatItem(bool isUser, string text)
    {
        IsUser = isUser;
        _text = text;
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
