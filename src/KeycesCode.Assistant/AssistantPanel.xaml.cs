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
    // Au-delà, le fichier envoyé à l'IA est coupé (Gemini accepte de très longs textes)
    private const int MaxContextCharacters = 200_000;

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

    // Fourni par l'application : le fichier ouvert dans l'éditeur et la sélection (null si aucun fichier)
    public Func<EditorContext?>? ContextProvider { get; set; }

    // Fourni par l'application : le dossier ouvert (null si aucun) — l'assistant voit tout le projet
    public Func<string?>? WorkspaceProvider { get; set; }

    // Mémoire de la discussion envoyée à chaque question : les derniers échanges seulement,
    // sinon chaque question devient plus longue (et plus lente) que la précédente
    private const int MaxHistoryMessages = 12;
    private const int MaxHistoryMessageLength = 6_000;

    // Levé quand on clique sur la croix de l'en-tête « Chat »
    public event EventHandler? CloseRequested;

    // Levé quand on clique sur « Insérer », « Remplacer la sélection » ou « Remplacer le fichier »
    public event EventHandler<CodeActionEventArgs>? CodeActionRequested;

    // getModel : fonction qui donne le modèle choisi dans les Paramètres
    public void Initialize(IAssistantService assistant, Func<string> getModel)
    {
        _assistant = assistant;
        _getModel = getModel;
        _ = CheckStatusAsync();
    }

    // ----- Identité et consignes de l'IA -----

    // Consignes données à l'IA : qui il est (l'assistant de Keyce'sCode, propulsé par Gemini)
    // et, en mode « écrire dans l'éditeur », comment marquer le code qu'il veut y écrire
    private static string BuildSystemPrompt(string model, bool writeToEditor)
    {
        var prompt = new StringBuilder();
        prompt.Append(
            "You are \"Keyce'sCode Assistant\", the AI coding assistant built into Keyce'sCode, " +
            "a code editor made by a group of Keyce students. " +
            $"You are powered by Google Gemini (model \"{model}\"), called through the Gemini API. " +
            "If someone asks who you are or which AI you use, answer: you are the Keyce'sCode assistant, " +
            $"powered by Google's Gemini model {model}. Never claim to be created by Anthropic, OpenAI or another company. " +
            "You help students write, explain, fix and improve code in any language (C#, Python, Java, C, C++, " +
            "JavaScript, HTML, CSS, PHP, SQL...). " +
            "When the content of the user's open file is given, read it carefully and base your answer on it. " +
            "When the project folder is given (file list and file contents), use it: you know the whole project, " +
            "not only the open file. " +
            "Keep explanations short, clear and pedagogical. Always answer in French. ");

        if (writeToEditor)
        {
            prompt.Append(
                "\n\nYou can WRITE DIRECTLY INTO THE USER'S EDITOR. When the user asks you to write, create, fix, " +
                "complete, comment, translate or improve code, do it yourself in the editor: put the code in a ``` block " +
                "whose opening line is the language, a colon and an action. Actions:\n" +
                "- ```python:replace-file  → the block becomes the WHOLE content of the open file. Give the COMPLETE file, " +
                "never \"...\" or \"rest unchanged\". Use it to fix or modify the open file.\n" +
                "- ```python:replace-selection  → the block replaces the text the user selected (only when a selection is given).\n" +
                "- ```python:insert  → the block is inserted at the cursor (to add a small piece of code).\n" +
                "- ```python:file=relative/path.py  → writes the block as the WHOLE content of that project file " +
                "(relative to the project folder, e.g. file=src/utils.py). The file is created if it does not exist, " +
                "and it works even if the file is NOT open. Use it to create files or to modify any other file of the project " +
                "(always the COMPLETE file content).\n" +
                "Use the real language name (csharp, python, html, java, c, cpp, javascript, css, php, sql...). " +
                "Use at most ONE action block per file. For examples or explanations that must NOT be written in the editor, " +
                "use a normal ``` block without action. After the block, say in one or two French sentences what you changed. " +
                "Never tell the user to copy and paste: the code is written automatically.");
        }
        else
        {
            prompt.Append(
                "When you write or fix code, give the COMPLETE code in ONE ``` block with the language name, " +
                "so the user can insert it into the editor with one click.");
        }

        return prompt.ToString();
    }

    // ----- Poser une question -----

    // Public : l'éditeur l'utilise aussi (clic droit → « Expliquer ce code »...)
    public async Task AskAsync(string question)
    {
        if (_assistant is null || _getModel is null)
            throw new InvalidOperationException("Appelle Initialize avant d'utiliser le panneau.");

        question = question.Trim();
        if (question.Length == 0 || IsBusy) return;

        string model = GeminiAssistantService.ResolveModel(_getModel());
        EditorContext? context = IncludeFileCheck.IsChecked == true ? ContextProvider?.Invoke() : null;
        string? workspace = ProjectCheck.IsChecked == true ? WorkspaceProvider?.Invoke() : null;
        bool writeToEditor = WriteToEditorCheck.IsChecked == true;

        ErrorText.Visibility = Visibility.Collapsed;
        string shown = context is null ? question : $"{question}\n📎 {context.FileName}" +
                       (context.Selection.Length > 0 ? " (sélection)" : "");
        if (workspace is not null) shown += $"\n📁 {Path.GetFileName(workspace.TrimEnd('\\', '/'))}";
        _items.Add(new ChatItem(isUser: true, shown));

        // En attendant le premier mot : petit message pour montrer que ça travaille
        const string Waiting = "Gemini réfléchit…";
        var answer = new ChatItem(isUser: false, Waiting);
        _items.Add(answer);
        UpdateEmpty();
        MessagesScroll.ScrollToEnd();

        var full = new StringBuilder();
        bool finished = false;
        _cts = new CancellationTokenSource();
        SetBusy(true);

        // Compteur « Gemini réfléchit… 3 s » jusqu'au premier mot : on voit que ça avance
        var waitWatch = System.Diagnostics.Stopwatch.StartNew();
        var waitTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        waitTimer.Tick += (_, _) =>
        {
            if (full.Length == 0) answer.Text = $"{Waiting} {waitWatch.Elapsed.TotalSeconds:0} s";
            else waitTimer.Stop();
        };
        waitTimer.Start();

        try
        {
            // Le dossier du projet est lu en arrière-plan (la fenêtre ne se fige pas)
            string project = workspace is null
                ? ""
                : await Task.Run(() => ProjectContext.Build(workspace, question, context?.FileName), _cts.Token);

            // Consignes + derniers échanges + question (avec le projet et le fichier ouvert)
            var messages = new List<ChatMessage> { new(ChatRole.System, BuildSystemPrompt(model, writeToEditor)) };
            messages.AddRange(_history);
            messages.Add(new ChatMessage(ChatRole.User, WithContext(question, context, project, writeToEditor)));

            // Chaque morceau arrive dès que Gemini l'a produit. Pour rester fluide, l'écran
            // n'est redessiné qu'environ 15 fois par seconde (et pas à chaque petit morceau).
            var lastRefresh = System.Diagnostics.Stopwatch.StartNew();
            await foreach (var piece in _assistant.StreamChatAsync(model, messages, _cts.Token))
            {
                bool first = full.Length == 0;
                full.Append(piece);
                if (first || lastRefresh.ElapsedMilliseconds >= 66)
                {
                    answer.Text = full.ToString();
                    MessagesScroll.ScrollToEnd();
                    lastRefresh.Restart();
                }
            }
            answer.Text = full.ToString();
            finished = true;
        }
        catch (OperationCanceledException)
        {
            answer.Text = full + "\n\n(Réponse arrêtée)";
        }
        catch (AssistantException ex)
        {
            AnswerError(answer, full, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            AnswerError(answer, full, "La connexion avec Gemini a été coupée. Vérifie Internet puis repose ta question.");
        }
        catch (ArgumentException ex)
        {
            AnswerError(answer, full, ex.Message);
        }
        finally
        {
            waitTimer.Stop();
            if (full.Length == 0)
            {
                // Pas de réponse : on retire la bulle vide (ou celle qui affiche encore « réfléchit… »)
                if (answer.Text.Length == 0 || answer.Text.StartsWith(Waiting, StringComparison.Ordinal)) _items.Remove(answer);
                else answer.Complete();
            }
            else
            {
                // L'historique garde la question sans le fichier, et seulement les derniers échanges
                RememberExchange(question, full.ToString());
                answer.Complete();   // découpe la réponse en texte + blocs de code avec boutons

                // Mode « écrire dans l'éditeur » : les blocs marqués sont écrits tout de suite
                if (finished && writeToEditor) ApplyActions(answer);
            }

            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            UpdateEmpty();
            MessagesScroll.ScrollToEnd();
            InputBox.Focus();
        }
    }

    // Erreur : affichée dans la bulle de réponse (la question suivante marche normalement)
    private static void AnswerError(ChatItem answer, StringBuilder full, string message)
    {
        full.Clear();
        answer.Text = "⚠️ " + message;
    }

    private void RememberExchange(string question, string answer)
    {
        if (answer.Length > MaxHistoryMessageLength)
            answer = answer[..MaxHistoryMessageLength] + "\n... (suite de la réponse non conservée)";

        _history.Add(new ChatMessage(ChatRole.User, question));
        _history.Add(new ChatMessage(ChatRole.Assistant, answer));

        while (_history.Count > MaxHistoryMessages) _history.RemoveAt(0);
    }

    // Ajoute le projet, le fichier ouvert (et la sélection) devant la question
    private static string WithContext(string question, EditorContext? context, string project, bool writeToEditor)
    {
        if (context is null)
        {
            string intro = project.Length > 0 ? project + "\n\n" : "";
            return writeToEditor
                ? intro + "[No file is open in the editor: to write code, use ```language:file=relative/path.ext]\n\n[Question]\n" + question
                : intro + "[Question]\n" + question;
        }

        string content = context.Content;
        bool cut = content.Length > MaxContextCharacters;
        if (cut) content = content[..MaxContextCharacters];

        var text = new StringBuilder();
        if (project.Length > 0)
        {
            text.AppendLine(project);
            text.AppendLine();
        }
        text.AppendLine($"[File open in the editor: {context.FileName} ({context.Language})]");
        text.AppendLine($"```{context.Language}");
        text.AppendLine(content);
        if (cut) text.AppendLine("... (file truncated)");
        text.AppendLine("```");

        if (context.Selection.Length > 0)
        {
            text.AppendLine("[Text selected by the user — the question is mainly about it; use :replace-selection to change only this part]");
            text.AppendLine($"```{context.Language}");
            text.AppendLine(context.Selection);
            text.AppendLine("```");
        }

        text.AppendLine();
        text.AppendLine("[Question]");
        text.Append(question);
        return text.ToString();
    }

    public void Stop() => _cts?.Cancel();

    // Met le curseur dans la zone de saisie (à l'ouverture du chat)
    public void FocusInput() => Dispatcher.InvokeAsync(() => { InputBox.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);

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

        var model = GeminiAssistantService.ResolveModel(_getModel());
        StatusText.Text = "Connexion à Gemini…";

        try
        {
            // Liste des modèles : vérifie à la fois Internet et la clé API
            await _assistant.GetModelsAsync();
            StatusText.Text = $"● Google Gemini · {model}";
        }
        catch (AssistantException ex)
        {
            StatusText.Text = "○ " + ex.Message;
        }
    }

    // Met à jour « Fichier ouvert : ... » au-dessus de la zone de saisie
    public void RefreshContext()
    {
        string? workspace = WorkspaceProvider?.Invoke();
        ProjectCheck.IsEnabled = workspace is not null;
        ProjectLabel.Text = workspace is null ? "Aucun dossier" : Path.GetFileName(workspace.TrimEnd('\\', '/'));

        EditorContext? context = ContextProvider?.Invoke();
        IncludeFileCheck.IsEnabled = context is not null;
        // Pastille « + index.php » de la carte de saisie, comme le Chat de VS Code
        ContextLabel.Text = context is null
            ? "Aucun fichier ouvert"
            : context.Selection.Length > 0
                ? $"{context.FileName} (sélection)"
                : context.FileName;
    }

    // ----- Événements de l'interface -----

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendInputAsync();

    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void NewConversation_Click(object sender, RoutedEventArgs e) => NewConversation();

    private async void CheckStatus_Click(object sender, RoutedEventArgs e) => await CheckStatusAsync();

    private void Panel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) RefreshContext();
    }

    private void InputBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => RefreshContext();

    // Boutons d'un bloc de code : Appliquer, Insérer, Remplacer la sélection, Remplacer le fichier, Copier
    private void CodeAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatSegment segment, Tag: string action }) return;

        if (action == "Copy")
        {
            Clipboard.SetText(segment.Text);
            return;
        }

        CodeActionKind kind;
        if (action == "Apply") kind = segment.Action ?? CodeActionKind.InsertAtCursor;
        else if (!Enum.TryParse(action, out kind)) return;

        var args = new CodeActionEventArgs(segment.Text, kind, segment.FileName);
        CodeActionRequested?.Invoke(this, args);
        if (!args.Applied && args.Result.Length > 0) ShowError(args.Result);
    }

    // Écrit dans l'éditeur chaque bloc de la réponse qui porte une consigne (:replace-file, :new=...)
    private void ApplyActions(ChatItem answer)
    {
        foreach (ChatSegment segment in answer.Segments.ToList())
        {
            if (!segment.IsCode || segment.Action is not CodeActionKind kind) continue;

            var args = new CodeActionEventArgs(segment.Text, kind, segment.FileName);
            CodeActionRequested?.Invoke(this, args);

            if (args.Result.Length > 0)
                answer.AddNote(args.Applied ? "✏️ " + args.Result : "⚠️ " + args.Result);
        }
    }

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
