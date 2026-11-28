using KeycesCode.Assistant;

namespace KeycesCode.Tests.IA;

// Lecture des réponses de l'assistant : texte, blocs de code et consignes pour l'éditeur
public class ChatSegmentTests
{
    [Fact]
    public void Parse_BlocSansConsigne_NeDonneAucuneAction()
    {
        var segments = ChatSegment.Parse("Voici :\n```python\nprint('a')\n```\nFin.");

        Assert.Equal(3, segments.Count);
        Assert.True(segments[1].IsCode);
        Assert.Equal("python", segments[1].Language);
        Assert.Equal("print('a')", segments[1].Text);
        Assert.Null(segments[1].Action);
    }

    [Theory]
    [InlineData("replace-file", CodeActionKind.ReplaceFile)]
    [InlineData("replace-selection", CodeActionKind.ReplaceSelection)]
    [InlineData("insert", CodeActionKind.InsertAtCursor)]
    public void Parse_BlocAvecConsigne_DonneLAction(string marker, CodeActionKind expected)
    {
        var segments = ChatSegment.Parse($"```csharp:{marker}\nint x = 1;\n```");

        var code = Assert.Single(segments);
        Assert.Equal("csharp", code.Language);
        Assert.Equal(expected, code.Action);
        Assert.Equal("int x = 1;", code.Text);
    }

    [Fact]
    public void Parse_NouveauFichier_DonneLeNom()
    {
        var segments = ChatSegment.Parse("```python:new=src/jeu.py\nprint('jeu')\n```");

        var code = Assert.Single(segments);
        Assert.Equal(CodeActionKind.NewFile, code.Action);
        Assert.Equal("src/jeu.py", code.FileName);
        Assert.Contains("jeu.py", code.Header);
    }

    [Fact]
    public void Parse_FichierDuProjet_DonneLeChemin()
    {
        var code = Assert.Single(ChatSegment.Parse("```csharp:file=src/Models/User.cs\nclass User {}\n```"));

        Assert.Equal(CodeActionKind.NewFile, code.Action);
        Assert.Equal("src/Models/User.cs", code.FileName);
    }

    [Fact]
    public void Parse_ConsigneInconnue_EstIgnoree()
    {
        var code = Assert.Single(ChatSegment.Parse("```js:bidule\nlet a;\n```"));

        Assert.Null(code.Action);
        Assert.Equal("js", code.Language);
    }

    [Fact]
    public void AddNote_AjouteUneLigneALaFin()
    {
        var item = new ChatItem(isUser: false, "```python:insert\nx = 1\n```");
        item.Complete();
        item.AddNote("✏️ Écrit dans main.py");

        Assert.Equal(2, item.Segments.Count);
        Assert.Equal("✏️ Écrit dans main.py", item.Segments[^1].Text);
    }
}
