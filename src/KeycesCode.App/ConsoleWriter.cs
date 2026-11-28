using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace KeycesCode.App;

// Écrit du texte en couleur dans un RichTextBox en lecture seule
// (terminal, OUTPUT, DEBUG CONSOLE). Les couleurs sont des clés du thème
// (PrimaryText, ErrorBrush...) : elles changent avec le thème sombre / clair.
//
// Rapidité : le texte n'est pas ajouté ligne par ligne (chaque ajout redessine tout le terminal).
// Il est mis en attente puis ajouté d'un seul coup toutes les 40 ms, avec un seul défilement.
internal sealed class ConsoleWriter
{
    private const int MaxLines = 1500;   // au-delà, on retire les plus anciennes lignes

    private readonly RichTextBox _box;
    private readonly ScrollViewer? _scroller;   // si le RichTextBox est dans un ScrollViewer (terminal)
    private readonly Paragraph _paragraph = new() { Margin = new System.Windows.Thickness(0) };
    private readonly List<Piece> _pending = [];
    private readonly DispatcherTimer _flushTimer;
    private int _lines;

    // Un morceau en attente : du texte coloré, ou une fin de ligne
    private readonly record struct Piece(string Text, string ColorKey, bool Bold, bool NewLine);

    public ConsoleWriter(RichTextBox box, ScrollViewer? scroller = null)
    {
        _box = box;
        _scroller = scroller;
        _box.Document = new FlowDocument(_paragraph)
        {
            PagePadding = new System.Windows.Thickness(0)
        };

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background, box.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _flushTimer.Tick += (_, _) => Flush();
    }

    // Ajoute un morceau de texte sans aller à la ligne
    public void Write(string text, string colorKey, bool bold = false)
    {
        if (text.Length == 0) return;
        _pending.Add(new Piece(text, colorKey, bold, false));
        ScheduleFlush();
    }

    // Termine la ligne en cours (l'affichage se fait au prochain Flush)
    public void EndLine()
    {
        _pending.Add(new Piece("", "", false, true));
        ScheduleFlush();
    }

    public void WriteLine(string text, string colorKey, bool bold = false)
    {
        Write(text, colorKey, bold);
        EndLine();
    }

    public void Clear()
    {
        _pending.Clear();
        _flushTimer.Stop();
        _paragraph.Inlines.Clear();
        _lines = 0;
    }

    private void ScheduleFlush()
    {
        if (!_flushTimer.IsEnabled) _flushTimer.Start();
    }

    // Ajoute tout ce qui est en attente en une seule fois
    private void Flush()
    {
        _flushTimer.Stop();
        if (_pending.Count == 0) return;

        // Ne garder que la fin si un programme a affiché énormément de lignes d'un coup
        int start = 0;
        int newLines = 0;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (!_pending[i].NewLine) continue;
            if (++newLines > MaxLines)
            {
                start = i + 1;
                break;
            }
        }

        var text = new System.Text.StringBuilder();
        string? color = null;
        bool bold = false;

        void AddRun()
        {
            if (text.Length == 0 || color is null) return;
            var run = new Run(text.ToString());
            run.SetResourceReference(TextElement.ForegroundProperty, color);
            if (bold) run.FontWeight = System.Windows.FontWeights.SemiBold;
            _paragraph.Inlines.Add(run);
            text.Clear();
        }

        for (int i = start; i < _pending.Count; i++)
        {
            Piece piece = _pending[i];
            if (piece.NewLine)
            {
                AddRun();
                _paragraph.Inlines.Add(new LineBreak());
                _lines++;
                continue;
            }

            // Morceaux voisins de même couleur : un seul Run (moins d'objets à dessiner)
            if (piece.ColorKey != color || piece.Bold != bold) AddRun();
            color = piece.ColorKey;
            bold = piece.Bold;
            text.Append(piece.Text);
        }

        AddRun();
        _pending.Clear();

        while (_lines > MaxLines) RemoveOldestLine();

        if (_scroller is not null) _scroller.ScrollToEnd();
        else _box.ScrollToEnd();
    }

    private void RemoveOldestLine()
    {
        while (_paragraph.Inlines.FirstInline is { } first)
        {
            _paragraph.Inlines.Remove(first);
            if (first is LineBreak) break;
        }
        _lines--;
    }
}
