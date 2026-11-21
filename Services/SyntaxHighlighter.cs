using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.IO;
using System.Xml;

namespace Keyce_sCode.Services;

public interface ISyntaxHighlighter
{
    IHighlightingDefinition? GetDefinition(string? filePath, bool isDarkTheme);
}

public sealed class LanguageSyntaxHighlighter : ISyntaxHighlighter
{
    private static readonly Dictionary<string, string> LanguageGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "python",
        [".cs"] = "csharp",
        [".js"] = "javascript",
        [".html"] = "html",
        [".css"] = "css",
        [".php"] = "php",
        [".c"] = "c",
        [".cpp"] = "cpp",
        [".h"] = "cpp",
        [".hpp"] = "cpp"
    };

    private readonly Dictionary<(string Language, bool Dark), IHighlightingDefinition> _definitions = new();

    public IHighlightingDefinition? GetDefinition(string? filePath, bool isDarkTheme)
    {
        if (filePath is null || !LanguageGroups.TryGetValue(Path.GetExtension(filePath), out var language))
        {
            return null;
        }

        var key = (language, isDarkTheme);
        if (!_definitions.TryGetValue(key, out var definition))
        {
            definition = LoadDefinition(language, isDarkTheme);
            _definitions.Add(key, definition);
        }

        return definition;
    }

    private static IHighlightingDefinition LoadDefinition(string language, bool isDarkTheme)
    {
        var colors = isDarkTheme
            ? new SyntaxColors("#569CD6", "#6A9955", "#CE9178", "#DCDCAA", "#B5CEA8", "#D4D4D4")
            : new SyntaxColors("#0000FF", "#008000", "#A31515", "#795E26", "#098658", "#202020");

        (string Keywords, string? CommentStart, string? BlockCommentStart, string? BlockCommentEnd, string? TagRule) syntax = language switch
        {
            "python" => ("and as assert async await break class continue def del elif else except False finally for from global if import in is lambda nonlocal not or pass raise return True try while with yield None", "#", null, null, null),
            "csharp" => ("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while async await var", "//", "/*", "*/", null),
            "javascript" => ("async await break case catch class const continue debugger default delete do else export extends false finally for from function if import in instanceof let new null of return static super switch this throw true try typeof undefined var void while with yield", "//", "/*", "*/", null),
            "html" => (string.Empty, null, "<!--", "-->", "&lt;/?[A-Za-z][^&gt;]*&gt;"),
            "css" => ("important inherit initial unset auto none block flex grid inline inline-block absolute relative fixed sticky transparent solid hidden", null, "/*", "*/", null),
            "php" => ("abstract and array as break callable case catch class clone const continue declare default do echo else elseif empty endfor endforeach endif endswitch endwhile eval exit extends final finally for foreach function global if implements include include_once instanceof insteadof interface isset list namespace new or print private protected public require require_once return static switch throw trait try unset use var while xor yield true false null", "//", "/*", "*/", null),
            "c" or "cpp" => ("alignas alignof auto bool break case char class const constexpr continue default delete do double else enum explicit extern false float for friend if inline int long namespace new nullptr operator private protected public register return short signed sizeof static struct switch template this throw true try typedef typename union unsigned using virtual void volatile while", "//", "/*", "*/", null),
            _ => (string.Empty, null, null, null, null)
        };
        var (keywords, commentStart, blockCommentStart, blockCommentEnd, tagRule) = syntax;

        var xml = $"""
            <SyntaxDefinition name="Keyce-{language}-{(isDarkTheme ? "dark" : "light")}" extensions="" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Keyword" foreground="{colors.Keyword}" fontWeight="bold" />
              <Color name="Comment" foreground="{colors.Comment}" />
              <Color name="String" foreground="{colors.String}" />
              <Color name="Tag" foreground="{colors.Tag}" />
              <Color name="Number" foreground="{colors.Number}" />
              <Color name="Default" foreground="{colors.Default}" />
              <RuleSet>
                {(keywords.Length == 0 ? string.Empty : $"<Keywords color=\"Keyword\">{string.Join(string.Empty, keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => $"<Word>{word}</Word>"))}</Keywords>")}
                {(commentStart is null ? string.Empty : $"<Span color=\"Comment\" begin=\"{commentStart}\" />")}
                {(blockCommentStart is null ? string.Empty : $"<Span color=\"Comment\" begin=\"{blockCommentStart}\" end=\"{blockCommentEnd}\" multiline=\"true\" />")}
                <Span color="String" begin="&quot;" end="&quot;" />
                <Span color="String" begin="'" end="'" />
                {(tagRule is null ? string.Empty : $"<Rule color=\"Tag\">{tagRule}</Rule>")}
                <Rule color="Number">\b\d+(\.\d+)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;

        using var reader = XmlReader.Create(new StringReader(xml));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private sealed record SyntaxColors(string Keyword, string Comment, string String, string Tag, string Number, string Default);
}
