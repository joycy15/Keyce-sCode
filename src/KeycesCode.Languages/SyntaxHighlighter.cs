using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Xml;

namespace KeycesCode.Languages;

public interface ISyntaxHighlighter
{
    IHighlightingDefinition? GetDefinition(string? filePath, bool isDarkTheme);
}

public sealed class LanguageSyntaxHighlighter : ISyntaxHighlighter
{
    private static readonly Dictionary<string, string> LanguageDefinitions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "Python",
        [".cs"] = "C#",
        [".js"] = "JavaScript",
        [".mjs"] = "JavaScript",
        [".cjs"] = "JavaScript",
        [".ts"] = "JavaScript",
        [".mts"] = "JavaScript",
        [".cts"] = "JavaScript",
        [".tsx"] = "JavaScript",
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".css"] = "CSS",
        [".php"] = "PHP",
        [".java"] = "Java",
        [".c"] = "C++",
        [".cpp"] = "C++",
        [".cc"] = "C++",
        [".cxx"] = "C++",
        [".h"] = "C++",
        [".hpp"] = "C++",
        [".hh"] = "C++",
        [".hxx"] = "C++"
    };

    private readonly Dictionary<(string Language, bool Dark), IHighlightingDefinition> _definitions = new();

    public IHighlightingDefinition? GetDefinition(string? filePath, bool isDarkTheme)
    {
        if (filePath is null || !LanguageDefinitions.TryGetValue(Path.GetExtension(filePath), out var language))
        {
            return null;
        }

        var key = (language, isDarkTheme);
        if (!_definitions.TryGetValue(key, out var definition))
        {
            definition = language switch
            {
                "HTML" => LoadHtmlDefinition(isDarkTheme),
                // C# et Java : on ajoute une règle qui colore les noms de types (Console, Random, String...)
                "C#" => LoadWithTypeNames("CSharp-Mode.xshd") ?? HighlightingManager.Instance.GetDefinition(language),
                _ => HighlightingManager.Instance.GetDefinition(language)
            };
            _definitions.Add(key, definition);
        }

        ApplyTheme(definition, isDarkTheme);
        if (language == "HTML")
        {
            ApplyTheme(HighlightingManager.Instance.GetDefinition("JavaScript"), isDarkTheme);
            ApplyTheme(HighlightingManager.Instance.GetDefinition("CSS"), isDarkTheme);
        }

        return definition;
    }

    private static IHighlightingDefinition LoadHtmlDefinition(bool isDarkTheme)
    {
        const string resourceName = "ICSharpCode.AvalonEdit.Highlighting.Resources.HTML-Mode.xshd";
        using var resource = typeof(HighlightingManager).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"La grammaire HTML intégrée est introuvable : {resourceName}");
        var syntax = new XmlDocument();
        syntax.Load(resource);

        var root = syntax.DocumentElement
            ?? throw new InvalidDataException("La grammaire HTML intégrée est vide.");
        var namespaceUri = root.NamespaceURI;
        var namespaces = new XmlNamespaceManager(syntax.NameTable);
        namespaces.AddNamespace("x", namespaceUri);

        var mainRules = root.SelectSingleNode("x:RuleSet[not(@name)]", namespaces)
            ?? throw new InvalidDataException("La grammaire HTML intégrée ne contient pas de règles principales.");
        var color = syntax.CreateElement("Color", namespaceUri);
        color.SetAttribute("name", "StyleTag");
        color.SetAttribute("foreground", isDarkTheme ? "#4EC9B0" : "#800000");
        root.InsertBefore(color, mainRules);

        var styleSpan = syntax.CreateElement("Span", namespaceUri);
        styleSpan.SetAttribute("ruleSet", "CSSSet");
        styleSpan.SetAttribute("multiline", "true");
        var begin = syntax.CreateElement("Begin", namespaceUri);
        begin.SetAttribute("color", "StyleTag");
        begin.InnerText = "<style(\\s[^>]*)?>";
        var end = syntax.CreateElement("End", namespaceUri);
        end.SetAttribute("color", "StyleTag");
        end.InnerText = "</style\\s*>";
        styleSpan.AppendChild(begin);
        styleSpan.AppendChild(end);

        var firstRule = mainRules.ChildNodes.OfType<XmlElement>().FirstOrDefault();
        mainRules.InsertBefore(styleSpan, firstRule);

        var cssRules = syntax.CreateElement("RuleSet", namespaceUri);
        cssRules.SetAttribute("name", "CSSSet");
        var cssImport = syntax.CreateElement("Import", namespaceUri);
        cssImport.SetAttribute("ruleSet", "CSS/");
        cssRules.AppendChild(cssImport);
        root.AppendChild(cssRules);

        using var reader = XmlReader.Create(new StringReader(syntax.OuterXml));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    // Charge une grammaire intégrée d'AvalonEdit et ajoute la couleur « TypeName » :
    // tout mot qui commence par une majuscule (Console, Random, List...) est colorié comme un type.
    private static IHighlightingDefinition? LoadWithTypeNames(string resourceFile)
    {
        string resourceName = "ICSharpCode.AvalonEdit.Highlighting.Resources." + resourceFile;
        using var resource = typeof(HighlightingManager).Assembly.GetManifestResourceStream(resourceName);
        if (resource is null) return null;

        var syntax = new XmlDocument();
        syntax.Load(resource);
        var root = syntax.DocumentElement;
        if (root is null) return null;

        var namespaceUri = root.NamespaceURI;
        var namespaces = new XmlNamespaceManager(syntax.NameTable);
        namespaces.AddNamespace("x", namespaceUri);
        var mainRules = root.SelectSingleNode("x:RuleSet[not(@name)]", namespaces);
        if (mainRules is null) return null;

        var color = syntax.CreateElement("Color", namespaceUri);
        color.SetAttribute("name", "TypeName");
        color.SetAttribute("foreground", "#4EC9B0");
        root.InsertBefore(color, mainRules);

        // Ajoutée à la fin : les mots-clés et les appels de méthode restent prioritaires
        var rule = syntax.CreateElement("Rule", namespaceUri);
        rule.SetAttribute("color", "TypeName");
        rule.InnerText = @"\b[A-Z][A-Za-z0-9_]*\b";
        mainRules.AppendChild(rule);

        using var reader = XmlReader.Create(new StringReader(syntax.OuterXml));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    // Public : l'application l'utilise aussi pour les grammaires intégrées (XML, Markdown, JSON...)
    public static void ApplyTheme(IHighlightingDefinition definition, bool isDarkTheme)
    {
        foreach (var color in definition.NamedHighlightingColors)
        {
            var foreground = GetThemeColor(color.Name, isDarkTheme);
            if (foreground is not null)
            {
                color.Foreground = new SimpleHighlightingBrush(foreground.Value);
            }
            else if (color.Foreground?.GetColor(null) is Color original && !IsReadable(original, isDarkTheme))
            {
                // Couleur prévue pour un fond blanc (ex. noir) : invisible sur fond sombre, et inversement
                color.Foreground = new SimpleHighlightingBrush(
                    (Color)ColorConverter.ConvertFromString(isDarkTheme ? "#D4D4D4" : "#1F1F1F"));
            }
        }
    }

    // Une couleur est lisible si sa luminosité contraste avec le fond de l'éditeur
    private static bool IsReadable(Color color, bool isDarkTheme)
    {
        double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
        return isDarkTheme ? luminance > 0.35 : luminance < 0.75;
    }

    // Palette inspirée de VS Code (Dark+ et Light+). L'ordre compte : la première règle qui correspond gagne.
    private static readonly (string[] Words, string Dark, string Light)[] Palette =
    [
        (["Comment"], "#6A9955", "#008000"),
        (["AttributeValue", "String", "Char", "Regex", "Quote"], "#CE9178", "#A31515"),
        (["Number", "Digit"], "#B5CEA8", "#098658"),
        (["Goto", "Exception", "Control", "Flow", "Preprocessor"], "#C586C0", "#AF00DB"),
        (["TypeName", "Builtin"], "#4EC9B0", "#267F99"),
        (["Keyword", "Modifier", "Visibility", "Literal", "Intrinsic", "TrueFalse", "Null",
          "This", "GetSet", "Checked", "Unsafe"], "#569CD6", "#0000FF"),
        (["Method", "Function"], "#DCDCAA", "#795E26"),
        (["Attribute"], "#9CDCFE", "#E50000"),
        (["Tag", "Entity", "DocType", "Declaration"], "#569CD6", "#800000"),
        (["Property"], "#9CDCFE", "#E50000"),
        (["Value", "Unit"], "#CE9178", "#0451A5"),
        (["Selector", "Class", "ID", "Pseudo"], "#D7BA7D", "#800000"),
        (["Brace", "Colon", "Punctuation", "Operator", "Slash", "Assignment"], "#D4D4D4", "#1F1F1F"),
    ];

    private static Color? GetThemeColor(string name, bool isDarkTheme)
    {
        foreach (var (words, dark, light) in Palette)
        {
            if (words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                return (Color)ColorConverter.ConvertFromString(isDarkTheme ? dark : light);
        }
        return null;
    }
}
