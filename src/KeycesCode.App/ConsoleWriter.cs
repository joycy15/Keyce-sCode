using System.Windows.Controls;
using System.Windows.Documents;

namespace KeycesCode.App;

// Écrit du texte en couleur dans un RichTextBox en lecture seule
// (terminal, OUTPUT, DEBUG CONSOLE). Les couleurs sont des clés du thème
// (PrimaryText, ErrorBrush...) : elles changent avec le thème sombre / clair.
internal sealed class ConsoleWriter
{
    private const int MaxLines = 3000;   // au-delà, on retire les plus anciennes lignes

    private readonly RichTextBox _box;
    private readonly ScrollViewer? _scroller;   // si le RichTextBox est dans un ScrollViewer (terminal)
    private readonly Paragraph _paragraph = new() { Margin = new System.Windows.Thickness(0) };
    private int _lines;

    public ConsoleWriter(RichTextBox box, ScrollViewer? scroller = null)
    {
        _box = box;
        _scroller = scroller;
        _box.Document = new FlowDocument(_paragraph)
        {
            PagePadding = new System.Windows.Thickness(0)
        };
    }

    // Ajoute un morceau de texte sans aller à la ligne
    public void Write(string text, string colorKey, bool bold = false)
    {
        var run = new Run(text);
        run.SetResourceReference(TextElement.ForegroundProperty, colorKey);
        if (bold) run.FontWeight = System.Windows.FontWeights.SemiBold;
        _paragraph.Inlines.Add(run);
    }

    // Termine la ligne en cours et fait défiler vers le bas
    public void EndLine()
    {
        _paragraph.Inlines.Add(new LineBreak());
        _lines++;
        if (_lines > MaxLines) RemoveOldestLine();
        if (_scroller is not null) _scroller.ScrollToEnd();
        else _box.ScrollToEnd();
    }

    public void WriteLine(string text, string colorKey, bool bold = false)
    {
        Write(text, colorKey, bold);
        EndLine();
    }

    public void Clear()
    {
        _paragraph.Inlines.Clear();
        _lines = 0;
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
