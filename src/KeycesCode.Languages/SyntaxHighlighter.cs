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
            definition = language == "HTML"
                ? LoadHtmlDefinition(isDarkTheme)
                : HighlightingManager.Instance.GetDefinition(language);
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

    private static Color? GetThemeColor(string name, bool isDarkTheme)
    {
        // Ponctuation (accolades, deux-points, points-virgules...) : couleur normale du texte
        if (name.Contains("Brace", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Colon", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Punctuation", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Operator", StringComparison.OrdinalIgnoreCase))
        {
            return (Color)ColorConverter.ConvertFromString(isDarkTheme ? "#D4D4D4" : "#1F1F1F");
        }

        // CSS : propriétés (margin, color...) et valeurs (0, auto, bold...), comme dans VS Code
        if (name.Equals("Property", StringComparison.OrdinalIgnoreCase))
        {
            return (Color)ColorConverter.ConvertFromString(isDarkTheme ? "#9CDCFE" : "#E50000");
        }

        if (name.Equals("Value", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Unit", StringComparison.OrdinalIgnoreCase))
        {
            return (Color)ColorConverter.ConvertFromString(isDarkTheme ? "#CE9178" : "#0451A5");
        }

        var color = name.Contains("Comment", StringComparison.OrdinalIgnoreCase)
            ? isDarkTheme ? "#6A9955" : "#008000"
            : name.Contains("String", StringComparison.OrdinalIgnoreCase) ||
              name.Contains("Character", StringComparison.OrdinalIgnoreCase) ||
              name.Contains("Char", StringComparison.OrdinalIgnoreCase) ||
              name.Contains("Regex", StringComparison.OrdinalIgnoreCase)
                ? isDarkTheme ? "#CE9178" : "#A31515"
                : name.Contains("Number", StringComparison.OrdinalIgnoreCase) ||
                  name.Contains("Digit", StringComparison.OrdinalIgnoreCase)
                    ? isDarkTheme ? "#B5CEA8" : "#098658"
                    : name.Contains("Keyword", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("Literal", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("Intrinsic", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("GlobalFunction", StringComparison.OrdinalIgnoreCase)
                        ? isDarkTheme ? "#569CD6" : "#0000FF"
                        : name.Contains("Method", StringComparison.OrdinalIgnoreCase)
                            ? isDarkTheme ? "#DCDCAA" : "#795E26"
                            : name.Contains("Tag", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("Attribute", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("Entity", StringComparison.OrdinalIgnoreCase)
                                ? isDarkTheme ? "#4EC9B0" : "#800000"
                                : name.Contains("Class", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("Selector", StringComparison.OrdinalIgnoreCase)
                                    ? isDarkTheme ? "#D7BA7D" : "#800080"
                                    : null;

        return color is null
            ? null
            : (Color)ColorConverter.ConvertFromString(color);
    }
}
