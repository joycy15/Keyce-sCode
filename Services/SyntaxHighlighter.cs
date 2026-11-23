using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Xml;

namespace Keyce_sCode.Services;

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

    private static void ApplyTheme(IHighlightingDefinition definition, bool isDarkTheme)
    {
        foreach (var color in definition.NamedHighlightingColors)
        {
            var foreground = GetThemeColor(color.Name, isDarkTheme);
            if (foreground is not null)
            {
                color.Foreground = new SimpleHighlightingBrush(foreground.Value);
            }
        }
    }

    private static Color? GetThemeColor(string name, bool isDarkTheme)
    {
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
