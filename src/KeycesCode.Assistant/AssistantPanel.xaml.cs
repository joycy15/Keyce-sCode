using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeycesCode.Core.Abstractions;
using KeycesCode.Core.Models;

namespace KeycesCode.Assistant;

public partial class AssistantPanel : UserControl
{
    // Consignes données à l'IA au début de chaque échange
    // Consignes données à l'IA (en anglais : les petits modèles les suivent mieux)
    private const string SystemPrompt =
        "You are the coding assistant built into Keyce'sCode, a code editor. " +
        "You help students write, explain, fix and improve code in any programming language " +
        "(C#, Python, Java, C, JavaScript, SQL...). Programming requests are always allowed: " +
        "never refuse them. If a request is unclear, guess the most likely meaning and answer. " +
        "Always answer in French. Put code in ``` blocks with the language name.";

    private readonly ObservableCollection<ChatItem> _items = [];   // ce qui est affiché
    private readonly List<ChatMessage> _history = [];               // ce qu'on renvoie à l'IA
    private IAssistantService? _assistant;
    private Func<string>? _getModel;
    private CancellationTokenSource? _cts;

    public AssistantPanel()
    {
        InitializeComponent();
        MessagesList.ItemsSource = _items;
    }

    public bool IsBusy => _cts is not null;

    // getModel : fonction qui donne le modèle choisi dans les Paramètres
    public void Initialize(IAssistantService assistant, Func<string> getModel)
    {
        _assistant = assistant;
        _getModel = getModel;
        _ = CheckStatusAsync();
    }

    // Public : l'éditeur pourra envoyer « Explique ce code : ... »
    public async Task AskAsync(string question)
    {
        if (_assistant is null || _getModel is null)
            throw new InvalidOperationException("Appelle Initialize avant d'utiliser le panneau.");

        question = question.Trim();
        if (question.Length == 0 || IsBusy) return;

        ErrorText.Visibility = Visibility.Collapsed;
        _items.Add(new ChatItem(isUser: true, question));
        _history.Add(new ChatMessage(ChatRole.User, question));

        var answer = new ChatItem(isUser: false, "");
        _items.Add(answer);
        UpdateEmpty();
        MessagesScroll.ScrollToEnd();

        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt) };
        messages.AddRange(_history);

        var full = new StringBuilder();
        _cts = new CancellationTokenSource();
        SetBusy(true);

        try
        {
            // Chaque morceau arrive dès qu'Ollama l'a produit
            await foreach (var piece in _assistant.StreamChatAsync(_getModel(), messages, _cts.Token))
            {
                full.Append(piece);
                answer.Text = full.ToString();
                MessagesScroll.ScrollToEnd();
            }
        }
        catch (OperationCanceledException)
        {
            answer.Text = full + "\n\n(Réponse arrêtée)";
        }
        catch (AssistantException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            ShowError("La connexion avec Ollama a été coupée.");
        }
        catch (ArgumentException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            if (full.Length == 0)
            {
                // Pas de réponse : on retire la bulle vide et la question de l'historique
                if (answer.Text.Length == 0) _items.Remove(answer);
                _history.RemoveAt(_history.Count - 1);
            }
            else
            {
                _history.Add(new ChatMessage(ChatRole.Assistant, full.ToString()));
            }

            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            UpdateEmpty();
            InputBox.Focus();
        }
    }

    public void Stop() => _cts?.Cancel();

    public void NewConversation()
    {
        if (IsBusy) return;
        _items.Clear();
        _history.Clear();
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateEmpty();
    }

    public async Task CheckStatusAsync()
    {
        if (_assistant is null || _getModel is null) return;

        var model = _getModel();
        StatusText.Text = "Connexion à Ollama…";

        if (!await _assistant.IsAvailableAsync())
        {
            StatusText.Text = "○ Ollama n'est pas lancé";
            return;
        }

        try
        {
            var models = await _assistant.GetModelsAsync();
            StatusText.Text = models.Contains(model, StringComparer.OrdinalIgnoreCase)
                ? $"● {model} (local)"
                : $"○ Modèle « {model} » absent : ollama pull {model}";
        }
        catch (AssistantException ex)
        {
            StatusText.Text = "○ " + ex.Message;
        }
    }

    // ----- Événements de l'interface -----

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendInputAsync();

    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();

    private void NewConversation_Click(object sender, RoutedEventArgs e) => NewConversation();

    private async void CheckStatus_Click(object sender, RoutedEventArgs e) => await CheckStatusAsync();

    private async void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Entrée seule = envoyer ; Maj+Entrée = nouvelle ligne (géré par AcceptsReturn)
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendInputAsync();
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) =>
        Placeholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ----- Aides -----

    private async Task SendInputAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(InputBox.Text)) return;
        var text = InputBox.Text;
        InputBox.Clear();
        await AskAsync(text);
    }

    private void SetBusy(bool busy)
    {
        SendButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        NewButton.IsEnabled = !busy;
    }

    private void UpdateEmpty() =>
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
