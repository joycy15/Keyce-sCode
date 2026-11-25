using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Indentation.CSharp;

namespace KeycesCode.Languages;

// Aide à la saisie dans l'éditeur, comme dans VS Code :
//  - liste de suggestions (mots-clés, extraits, mots déjà présents dans le fichier) ; Ctrl+Espace pour l'ouvrir
//  - extraits avec Tab : « ! » en HTML (squelette complet), « cw » en C#, « main », « for »...
//  - abréviations HTML (Emmet simplifié) : div.carte#haut + Tab
//  - fermeture automatique des parenthèses, crochets, accolades, guillemets et balises HTML
//  - Entrée entre { } : le bloc s'ouvre sur trois lignes avec l'indentation
public sealed class CodeAssist
{
    private readonly TextEditor _editor;
    private readonly string _language;
    private CompletionWindow? _completion;

    private static readonly Dictionary<char, char> Pairs = new()
    {
        ['('] = ')', ['['] = ']', ['{'] = '}', ['"'] = '"', ['\''] = '\''
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"
    };

    private CodeAssist(TextEditor editor, string language)
    {
        _editor = editor;
        _language = language;

        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        if (language is "csharp" or "java" or "javascript" or "c" or "php" or "css")
            editor.TextArea.IndentationStrategy = new CSharpIndentationStrategy(editor.Options);

        editor.TextArea.TextEntering += OnTextEntering;
        editor.TextArea.TextEntered += OnTextEntered;
        editor.TextArea.PreviewKeyDown += OnPreviewKeyDown;
    }

    // Branche l'aide à la saisie sur un éditeur, selon l'extension du fichier
    public static CodeAssist Attach(TextEditor editor, string filePath) =>
        new(editor, LanguageOf(filePath));

    private static string LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".py" => "python",
        ".js" or ".mjs" or ".cjs" or ".ts" => "javascript",
        ".html" or ".htm" => "html",
        ".css" => "css",
        ".php" => "php",
        ".java" => "java",
        ".c" or ".h" or ".cpp" or ".cc" or ".hpp" => "c",
        ".xaml" or ".xml" or ".csproj" => "xml",
        _ => "text"
    };

    private TextArea Area => _editor.TextArea;
    private TextDocument Doc => _editor.Document;
    private int Caret => Area.Caret.Offset;
    private char CharAt(int offset) => offset >= 0 && offset < Doc.TextLength ? Doc.GetCharAt(offset) : '\0';

    // ---------------------------------------------------------------- frappe

    private void OnTextEntering(object? sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1) return;
        char c = e.Text[0];

        // Un caractère qui n'appartient pas à un mot ferme la liste de suggestions
        if (_completion is not null && !IsWordChar(c))
            _completion.Close();

        // On tape le caractère fermant déjà présent juste après le curseur : on le saute
        if (c is ')' or ']' or '}' or '"' or '\'' && CharAt(Caret) == c && Area.Selection.IsEmpty)
        {
            Area.Caret.Offset++;
            e.Handled = true;
            return;
        }

        if (!Pairs.TryGetValue(c, out char closing)) return;

        // Texte sélectionné : on l'entoure (ex. sélection + " → "sélection")
        if (!Area.Selection.IsEmpty)
        {
            string selected = Area.Selection.GetText();
            int start = Area.Selection.SurroundingSegment.Offset;
            Doc.Replace(Area.Selection.SurroundingSegment, c + selected + closing);
            Area.ClearSelection();
            Area.Caret.Offset = start + selected.Length + 2;
            e.Handled = true;
            return;
        }

        // Guillemets : pas après une lettre (l'apostrophe de « l'arbre » reste seule)
        if (c is '"' or '\'' && (IsWordChar(CharAt(Caret - 1)) || _language is "text")) return;

        // On ne ferme que si la suite est vide ou une ponctuation
        char next = CharAt(Caret);
        if (next != '\0' && !char.IsWhiteSpace(next) && ")]};,>".IndexOf(next) < 0) return;

        int offset = Caret;
        Doc.Insert(offset, $"{c}{closing}");
        Area.Caret.Offset = offset + 1;
        e.Handled = true;
    }

    private void OnTextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1) return;
        char c = e.Text[0];

        // HTML / XML : « > » ferme la balise ouverte (<div> → <div></div>)
        if (c == '>' && _language is "html" or "xml" or "php")
        {
            CloseTag();
            return;
        }

        // HTML : après « < », la liste des balises
        if (c == '<' && _language is "html" or "php")
        {
            ShowCompletion(Caret, HtmlTags.Select(t => Item(t, "balise")).ToList());
            return;
        }

        // HTML : « ! » en début de ligne propose le squelette complet (Entrée ou Tab pour l'insérer)
        if (c == '!' && _language is "html" or "php")
        {
            DocumentLine line = Doc.GetLineByOffset(Caret);
            if (Doc.GetText(line.Offset, Caret - line.Offset).Trim() == "!")
            {
                var doctype = HtmlSnippets["!"];
                ShowCompletion(Caret - 1,
                    [new CompletionItem("!", doctype.Description, doctype.Body, this, priority: 3)]);
            }
            return;
        }

        if (_completion is null && IsWordChar(c))
        {
            string prefix = WordBeforeCaret();
            if (prefix.Length >= 2) ShowCompletion(Caret - prefix.Length, Suggestions(prefix));
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Espace : ouvrir les suggestions
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            string prefix = WordBeforeCaret();
            ShowCompletion(Caret - prefix.Length, Suggestions(prefix));
            e.Handled = true;
            return;
        }

        if (_completion is not null) return;   // Tab et Entrée appartiennent alors à la liste

        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && Area.Selection.IsEmpty)
        {
            if (TryExpandAtCaret()) e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && Area.Selection.IsEmpty)
        {
            char before = CharAt(Caret - 1), after = CharAt(Caret);
            bool block = (before, after) is ('{', '}') or ('[', ']') or ('(', ')');
            bool tag = before == '>' && after == '<' && CharAt(Caret + 1) == '/';
            if (block || tag)
            {
                string indent = CurrentIndent();
                string unit = _editor.Options.IndentationString;
                int offset = Caret;
                Doc.Insert(offset, "\n" + indent + unit + "\n" + indent);
                Area.Caret.Offset = offset + 1 + indent.Length + unit.Length;
                e.Handled = true;
            }
            return;
        }

        // Retour arrière entre deux caractères appariés : on efface les deux
        if (e.Key == Key.Back && Area.Selection.IsEmpty && Caret > 0 &&
            Pairs.TryGetValue(CharAt(Caret - 1), out char closing) && CharAt(Caret) == closing)
        {
            Doc.Remove(Caret - 1, 2);
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- suggestions

    private void ShowCompletion(int startOffset, List<ICompletionData> items)
    {
        if (items.Count == 0) return;
        _completion?.Close();

        var window = new CompletionWindow(Area)
        {
            StartOffset = startOffset,
            MinWidth = 260,
            CloseWhenCaretAtBeginning = true
        };
        StyleWindow(window);
        foreach (var item in items) window.CompletionList.CompletionData.Add(item);

        string typed = Doc.GetText(startOffset, Caret - startOffset);
        window.Show();
        if (typed.Length > 0) window.CompletionList.SelectItem(typed);
        window.Closed += (_, _) => _completion = null;
        _completion = window;
    }

    // Couleurs de la liste prises dans le thème de l'application (sombre / clair)
    private void StyleWindow(CompletionWindow window)
    {
        Brush? background = _editor.TryFindResource("MenuPopupBackground") as Brush;
        Brush? foreground = _editor.TryFindResource("PrimaryText") as Brush;
        Brush? border = _editor.TryFindResource("StrongBorderBrush") as Brush;
        if (background is null || foreground is null) return;

        window.Background = background;
        window.BorderBrush = border ?? background;
        window.BorderThickness = new Thickness(1);
        window.CompletionList.ListBox.Background = background;
        window.CompletionList.ListBox.Foreground = foreground;
        window.CompletionList.ListBox.BorderThickness = new Thickness(0);
        window.CompletionList.ListBox.FontFamily = _editor.FontFamily;
        window.CompletionList.ListBox.FontSize = Math.Max(12, _editor.FontSize - 1);
    }

    private List<ICompletionData> Suggestions(string prefix)
    {
        var items = new List<ICompletionData>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(ICompletionData item)
        {
            if (seen.Add(item.Text) &&
                (prefix.Length == 0 || item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) &&
                item.Text != prefix)
            {
                items.Add(item);
            }
        }

        foreach (var (trigger, snippet) in SnippetsFor(_language))
            Add(new CompletionItem(trigger, snippet.Description, snippet.Body, this, priority: 2));

        // HTML : choisir « div » dans la liste écrit directement <div></div> (comme Emmet dans VS Code)
        if (_language is "html" or "php" && CharAt(Caret - prefix.Length - 1) != '<')
        {
            foreach (string tag in HtmlTags)
                if (ExpandHtmlAbbreviation(tag) is { } element)
                    Add(new CompletionItem(tag, "balise", element, this, priority: 1.5));
        }

        foreach (string word in KeywordsFor(_language))
            Add(Item(word, "mot-clé"));

        // Mots déjà présents dans le fichier (noms de variables, de fonctions...)
        foreach (Match m in Regex.Matches(Doc.Text, @"\b[A-Za-z_][A-Za-z0-9_]{2,}\b").Cast<Match>().Take(4000))
            Add(Item(m.Value, "dans le fichier", priority: 0));

        return items.OrderByDescending(i => i.Priority).ThenBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
                    .Take(200).ToList();
    }

    private ICompletionData Item(string text, string description, double priority = 1) =>
        new CompletionItem(text, description, null, this, priority);

    // ---------------------------------------------------------------- extraits (Tab)

    private bool TryExpandAtCaret()
    {
        DocumentLine line = Doc.GetLineByOffset(Caret);
        string before = Doc.GetText(line.Offset, Caret - line.Offset);

        // En HTML, l'abréviation peut contenir ! . # -
        var match = Regex.Match(before, _language is "html" or "php" ? @"[!\w.#\-]+$" : @"\w+$");
        if (!match.Success) return false;
        string word = match.Value;

        string? body = SnippetsFor(_language).TryGetValue(word, out var snippet) ? snippet.Body : null;
        if (body is null && _language is "html" or "php") body = ExpandHtmlAbbreviation(word);
        if (body is null) return false;

        InsertSnippet(Caret - word.Length, word.Length, body);
        return true;
    }

    // Insère un extrait : « \t » = un cran d'indentation, « $0 » = position finale du curseur
    internal void InsertSnippet(int offset, int length, string body)
    {
        string indent = Regex.Match(Doc.GetText(Doc.GetLineByOffset(offset)), @"^\s*").Value;
        string text = body.Replace("\t", _editor.Options.IndentationString)
                          .Replace("\n", "\n" + indent);
        int cursor = text.IndexOf("$0", StringComparison.Ordinal);
        text = text.Replace("$0", "");

        Doc.Replace(offset, length, text);
        Area.Caret.Offset = offset + (cursor >= 0 ? cursor : text.Length);
    }

    // div → <div></div> ; p.intro → <p class="intro"></p> ; section#haut.carte → <section id="haut" class="carte">
    private static string? ExpandHtmlAbbreviation(string word)
    {
        var m = Regex.Match(word, @"^([a-zA-Z][a-zA-Z0-9]*)?((?:[.#][\w-]+)*)$");
        if (!m.Success || word.Length == 0) return null;

        string tag = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value.ToLowerInvariant() : "div";
        if (m.Groups[1].Value.Length > 0 && !HtmlTags.Contains(tag)) return null;

        var classes = Regex.Matches(m.Groups[2].Value, @"\.([\w-]+)").Select(x => x.Groups[1].Value).ToList();
        var id = Regex.Match(m.Groups[2].Value, @"#([\w-]+)");

        string attributes = (id.Success ? $" id=\"{id.Groups[1].Value}\"" : "") +
                            (classes.Count > 0 ? $" class=\"{string.Join(' ', classes)}\"" : "");

        if (tag == "a") attributes = " href=\"$0\"" + attributes;
        if (tag == "img") return $"<img src=\"$0\" alt=\"\"{attributes}>";
        if (tag == "input") return $"<input type=\"$0\"{attributes}>";
        if (tag == "link") return "<link rel=\"stylesheet\" href=\"$0\">";
        if (VoidTags.Contains(tag)) return $"<{tag}{attributes}>$0";
        return tag == "a" ? $"<a{attributes}></a>" : $"<{tag}{attributes}>$0</{tag}>";
    }

    // ---------------------------------------------------------------- HTML : fermeture des balises

    private void CloseTag()
    {
        DocumentLine line = Doc.GetLineByOffset(Caret);
        string before = Doc.GetText(line.Offset, Caret - line.Offset);
        var m = Regex.Match(before, @"<([a-zA-Z][\w:-]*)(\s[^<>]*)?>$");
        if (!m.Success || before.EndsWith("/>", StringComparison.Ordinal)) return;

        string tag = m.Groups[1].Value;
        if (VoidTags.Contains(tag)) return;

        int offset = Caret;
        Doc.Insert(offset, $"</{tag}>");
        Area.Caret.Offset = offset;
    }

    // ---------------------------------------------------------------- utilitaires

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private string WordBeforeCaret()
    {
        int start = Caret;
        while (start > 0 && IsWordChar(CharAt(start - 1))) start--;
        return Doc.GetText(start, Caret - start);
    }

    private string CurrentIndent()
    {
        DocumentLine line = Doc.GetLineByOffset(Caret);
        return Regex.Match(Doc.GetText(line), @"^\s*").Value;
    }

    // ---------------------------------------------------------------- vocabulaire

    private sealed record Snippet(string Description, string Body);

    private static Dictionary<string, Snippet> SnippetsFor(string language) => language switch
    {
        "html" => HtmlSnippets,
        "php" => HtmlSnippets.Concat(PhpSnippets).ToDictionary(),
        "csharp" => CSharpSnippets,
        "python" => PythonSnippets,
        "javascript" => JsSnippets,
        "java" => JavaSnippets,
        "c" => CSnippets,
        "css" => CssSnippets,
        _ => new Dictionary<string, Snippet>()
    };

    private static readonly Dictionary<string, Snippet> HtmlSnippets = new()
    {
        ["!"] = new("Squelette HTML5 complet",
            "<!DOCTYPE html>\n<html lang=\"fr\">\n<head>\n\t<meta charset=\"UTF-8\">\n" +
            "\t<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n" +
            "\t<title>Document</title>\n</head>\n<body>\n\t$0\n</body>\n</html>"),
        ["html:5"] = new("Squelette HTML5 complet",
            "<!DOCTYPE html>\n<html lang=\"fr\">\n<head>\n\t<meta charset=\"UTF-8\">\n" +
            "\t<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n" +
            "\t<title>Document</title>\n</head>\n<body>\n\t$0\n</body>\n</html>"),
        ["link:css"] = new("Lien vers une feuille de style", "<link rel=\"stylesheet\" href=\"$0style.css\">"),
        ["script:src"] = new("Script externe", "<script src=\"$0\"></script>"),
        ["form"] = new("Formulaire", "<form action=\"$0\" method=\"post\">\n\t\n</form>"),
        ["ul"] = new("Liste à puces", "<ul>\n\t<li>$0</li>\n</ul>"),
        ["table"] = new("Tableau", "<table>\n\t<tr>\n\t\t<th>$0</th>\n\t</tr>\n\t<tr>\n\t\t<td></td>\n\t</tr>\n</table>"),
    };

    private static readonly Dictionary<string, Snippet> PhpSnippets = new()
    {
        ["php"] = new("Bloc PHP", "<?php\n$0\n?>"),
        ["echo"] = new("Afficher", "echo \"$0\";"),
        ["foreach"] = new("Boucle foreach", "foreach ($tableau as $element) {\n\t$0\n}"),
    };

    private static readonly Dictionary<string, Snippet> CSharpSnippets = new()
    {
        ["cw"] = new("Console.WriteLine", "Console.WriteLine($0);"),
        ["cr"] = new("Console.ReadLine", "Console.ReadLine()$0"),
        ["main"] = new("Méthode Main", "static void Main(string[] args)\n{\n\t$0\n}"),
        ["svm"] = new("Méthode Main", "static void Main(string[] args)\n{\n\t$0\n}"),
        ["prop"] = new("Propriété", "public int $0MaPropriete { get; set; }"),
        ["ctor"] = new("Constructeur", "public $0NomClasse()\n{\n\t\n}"),
        ["class"] = new("Classe", "public class $0NomClasse\n{\n\t\n}"),
        ["if"] = new("Condition if", "if ($0)\n{\n\t\n}"),
        ["else"] = new("Bloc else", "else\n{\n\t$0\n}"),
        ["for"] = new("Boucle for", "for (int i = 0; i < $0; i++)\n{\n\t\n}"),
        ["foreach"] = new("Boucle foreach", "foreach (var element in $0)\n{\n\t\n}"),
        ["while"] = new("Boucle while", "while ($0)\n{\n\t\n}"),
        ["try"] = new("try / catch", "try\n{\n\t$0\n}\ncatch (Exception ex)\n{\n\tConsole.WriteLine(ex.Message);\n}"),
        ["switch"] = new("switch", "switch ($0)\n{\n\tcase 1:\n\t\tbreak;\n\tdefault:\n\t\tbreak;\n}"),
    };

    private static readonly Dictionary<string, Snippet> PythonSnippets = new()
    {
        ["def"] = new("Fonction", "def $0nom():\n\tpass"),
        ["main"] = new("Point d'entrée", "def main():\n\t$0\n\n\nif __name__ == \"__main__\":\n\tmain()"),
        ["for"] = new("Boucle for", "for i in range($0):\n\t"),
        ["if"] = new("Condition", "if $0:\n\t"),
        ["while"] = new("Boucle while", "while $0:\n\t"),
        ["class"] = new("Classe", "class $0NomClasse:\n\tdef __init__(self):\n\t\tpass"),
        ["pr"] = new("print", "print($0)"),
        ["inp"] = new("input", "input(\"$0\")"),
        ["try"] = new("try / except", "try:\n\t$0\nexcept Exception as e:\n\tprint(e)"),
    };

    private static readonly Dictionary<string, Snippet> JsSnippets = new()
    {
        ["log"] = new("console.log", "console.log($0);"),
        ["fn"] = new("Fonction", "function $0nom() {\n\t\n}"),
        ["af"] = new("Fonction fléchée", "const $0nom = () => {\n\t\n};"),
        ["for"] = new("Boucle for", "for (let i = 0; i < $0; i++) {\n\t\n}"),
        ["if"] = new("Condition", "if ($0) {\n\t\n}"),
        ["qs"] = new("querySelector", "document.querySelector(\"$0\")"),
        ["ael"] = new("addEventListener", "addEventListener(\"click\", () => {\n\t$0\n});"),
    };

    private static readonly Dictionary<string, Snippet> JavaSnippets = new()
    {
        ["main"] = new("Méthode main", "public static void main(String[] args) {\n\t$0\n}"),
        ["psvm"] = new("Méthode main", "public static void main(String[] args) {\n\t$0\n}"),
        ["sout"] = new("System.out.println", "System.out.println($0);"),
        ["for"] = new("Boucle for", "for (int i = 0; i < $0; i++) {\n\t\n}"),
        ["if"] = new("Condition", "if ($0) {\n\t\n}"),
        ["class"] = new("Classe", "public class $0NomClasse {\n\t\n}"),
    };

    private static readonly Dictionary<string, Snippet> CSnippets = new()
    {
        ["main"] = new("Fonction main", "#include <stdio.h>\n\nint main(void)\n{\n\t$0\n\treturn 0;\n}"),
        ["inc"] = new("#include", "#include <$0stdio.h>"),
        ["pf"] = new("printf", "printf(\"$0\\n\");"),
        ["sf"] = new("scanf", "scanf(\"%d\", &$0);"),
        ["for"] = new("Boucle for", "for (int i = 0; i < $0; i++)\n{\n\t\n}"),
        ["if"] = new("Condition", "if ($0)\n{\n\t\n}"),
        ["while"] = new("Boucle while", "while ($0)\n{\n\t\n}"),
    };

    private static readonly Dictionary<string, Snippet> CssSnippets = new()
    {
        ["flex"] = new("Flexbox centré", "display: flex;\njustify-content: center;\nalign-items: center;$0"),
        ["grid"] = new("Grille", "display: grid;\ngrid-template-columns: repeat($03, 1fr);\ngap: 16px;"),
        ["media"] = new("Media query", "@media (max-width: 768px) {\n\t$0\n}"),
    };

    private static IEnumerable<string> KeywordsFor(string language) => language switch
    {
        "csharp" => CSharpKeywords,
        "python" => PythonKeywords,
        "javascript" => JsKeywords,
        "java" => JavaKeywords,
        "c" => CKeywords,
        "php" => PhpKeywords.Concat(HtmlTags),
        "html" => HtmlTags.Concat(HtmlAttributes),
        "css" => CssProperties,
        _ => []
    };

    private static readonly string[] CSharpKeywords =
    [
        "abstract", "async", "await", "base", "bool", "break", "case", "catch", "char", "class", "const",
        "continue", "decimal", "default", "do", "double", "else", "enum", "false", "finally", "float", "for",
        "foreach", "if", "in", "int", "interface", "internal", "is", "long", "namespace", "new", "null",
        "object", "out", "override", "private", "protected", "public", "readonly", "record", "ref", "return",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "using", "var", "virtual",
        "void", "while", "Console", "WriteLine", "ReadLine", "Write", "Math", "List", "Dictionary",
        "Random", "DateTime", "Exception", "Task", "Parse", "TryParse", "ToString", "Length", "Count"
    ];

    private static readonly string[] PythonKeywords =
    [
        "and", "as", "assert", "break", "class", "continue", "def", "elif", "else", "except", "False",
        "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "None", "not", "or",
        "pass", "raise", "return", "True", "try", "while", "with", "yield", "print", "input", "range",
        "len", "int", "float", "str", "list", "dict", "set", "tuple", "open", "enumerate", "append"
    ];

    private static readonly string[] JsKeywords =
    [
        "async", "await", "break", "case", "catch", "class", "const", "continue", "default", "else",
        "export", "false", "finally", "for", "function", "if", "import", "let", "new", "null", "return",
        "switch", "this", "throw", "true", "try", "typeof", "undefined", "var", "while", "console",
        "document", "window", "querySelector", "getElementById", "addEventListener", "innerHTML",
        "textContent", "fetch", "JSON", "parseInt", "setTimeout", "length", "push", "map", "filter"
    ];

    private static readonly string[] JavaKeywords =
    [
        "abstract", "boolean", "break", "case", "catch", "char", "class", "continue", "default", "do",
        "double", "else", "extends", "false", "final", "finally", "float", "for", "if", "implements",
        "import", "int", "interface", "long", "new", "null", "package", "private", "protected", "public",
        "return", "static", "String", "super", "switch", "this", "throw", "true", "try", "void", "while",
        "System", "Scanner", "ArrayList", "println", "nextInt", "nextLine"
    ];

    private static readonly string[] CKeywords =
    [
        "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum",
        "extern", "float", "for", "if", "include", "int", "long", "return", "short", "signed", "sizeof",
        "static", "struct", "switch", "typedef", "unsigned", "void", "while", "printf", "scanf", "malloc",
        "free", "strlen", "strcpy", "NULL", "std", "cout", "cin", "endl", "string", "vector", "class",
        "namespace", "using", "true", "false", "bool"
    ];

    private static readonly string[] PhpKeywords =
    [
        "echo", "print", "function", "return", "if", "else", "elseif", "foreach", "for", "while", "array",
        "isset", "empty", "include", "require", "require_once", "session_start", "header", "class", "public",
        "private", "new", "null", "true", "false", "mysqli_connect", "mysqli_query", "PDO", "prepare",
        "execute", "fetch", "fetchAll", "htmlspecialchars", "count", "strlen", "explode", "implode"
    ];

    private static readonly HashSet<string> HtmlTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "article", "aside", "audio", "b", "body", "br", "button", "canvas", "caption", "code",
        "div", "em", "fieldset", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "head",
        "header", "hr", "html", "i", "iframe", "img", "input", "label", "legend", "li", "link", "main",
        "meta", "nav", "ol", "option", "p", "pre", "script", "section", "select", "small", "span", "strong",
        "style", "table", "tbody", "td", "textarea", "th", "thead", "title", "tr", "u", "ul", "video"
    };

    private static readonly string[] HtmlAttributes =
    [
        "class", "id", "href", "src", "alt", "type", "name", "value", "placeholder", "action", "method",
        "required", "style", "title", "for", "rel", "target", "width", "height", "onclick", "disabled"
    ];

    private static readonly string[] CssProperties =
    [
        "align-items", "background", "background-color", "border", "border-radius", "bottom", "box-shadow",
        "color", "cursor", "display", "flex", "flex-direction", "flex-wrap", "font-family", "font-size",
        "font-weight", "gap", "grid-template-columns", "height", "justify-content", "left", "letter-spacing",
        "line-height", "margin", "margin-bottom", "margin-left", "margin-right", "margin-top", "max-width",
        "min-height", "opacity", "overflow", "padding", "padding-bottom", "padding-left", "padding-right",
        "padding-top", "position", "right", "text-align", "text-decoration", "text-transform", "top",
        "transition", "width", "z-index"
    ];

    // ---------------------------------------------------------------- élément de la liste

    private sealed class CompletionItem(string text, string description, string? snippet, CodeAssist owner, double priority)
        : ICompletionData
    {
        public ImageSource? Image => null;
        public string Text { get; } = text;
        public object Content => snippet is null ? Text : $"{Text}   ▸ {description}";
        public object Description { get; } = description;
        public double Priority { get; } = priority;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            if (snippet is not null)
                owner.InsertSnippet(completionSegment.Offset, completionSegment.Length, snippet);
            else
                textArea.Document.Replace(completionSegment, Text);
        }
    }
}
