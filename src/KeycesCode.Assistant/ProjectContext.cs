using System.IO;
using System.Text;

namespace KeycesCode.Assistant;

// Prépare ce que l'assistant voit du dossier ouvert : la liste des fichiers du projet,
// puis le contenu des fichiers texte (d'abord ceux cités dans la question), dans une limite de taille.
// Ainsi il peut répondre sur tout le projet et modifier un fichier qui n'est pas ouvert.
public static class ProjectContext
{
    // Dossiers jamais envoyés (énormes, générés automatiquement ou privés)
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".vscode", ".idea", "node_modules", "bin", "obj", "packages", "__pycache__",
        ".venv", "venv", "env", "dist", "build", "out", "publish", "target", ".next", "coverage"
    };

    // Fichiers texte qu'on peut lire et envoyer
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".xaml", ".py", ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx",
        ".html", ".htm", ".css", ".scss", ".php", ".java", ".c", ".h", ".cpp", ".hpp", ".cc", ".go", ".rs",
        ".rb", ".kt", ".swift", ".sql", ".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".md", ".txt",
        ".ps1", ".bat", ".cmd", ".sh", ".gitignore", ".env.example", ".config", ".props", ".targets"
    };

    // Fichiers jamais envoyés (secrets)
    private static readonly HashSet<string> SecretFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "settings.json", "appsettings.Development.json", "secrets.json", "credentials.json"
    };

    private const int MaxListedFiles = 400;
    private const int MaxFileSize = 60_000;   // un fichier plus gros n'est que listé

    public static string Build(string root, string question, string? openFileName, int budget = 80_000)
    {
        if (!Directory.Exists(root)) return "";

        List<string> files = ListFiles(root);
        var text = new StringBuilder();
        text.AppendLine($"[Project folder open in Keyce'sCode: {Path.GetFileName(root.TrimEnd('\\', '/'))}]");
        text.AppendLine("Files (relative paths):");
        foreach (string file in files.Take(MaxListedFiles))
            text.AppendLine("- " + Relative(root, file));
        if (files.Count > MaxListedFiles)
            text.AppendLine($"- ... ({files.Count - MaxListedFiles} more files)");

        // Ordre : fichiers cités dans la question, puis les plus petits (souvent les plus utiles)
        var ordered = files
            .Where(f => IsText(f) && !string.Equals(Path.GetFileName(f), openFileName, StringComparison.OrdinalIgnoreCase))
            .Select(f => (Path: f, Size: SafeSize(f)))
            .Where(f => f.Size is > 0 and <= MaxFileSize)
            .OrderByDescending(f => question.Contains(Path.GetFileName(f.Path), StringComparison.OrdinalIgnoreCase) ||
                                    question.Contains(Path.GetFileNameWithoutExtension(f.Path), StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.Size)
            .ToList();

        int used = text.Length;
        foreach (var (path, _) in ordered)
        {
            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (used + content.Length > budget) continue;
            text.AppendLine();
            text.AppendLine($"[File: {Relative(root, path)}]");
            text.AppendLine("```");
            text.AppendLine(content);
            text.AppendLine("```");
            used = text.Length;
        }

        return text.ToString();
    }

    public static List<string> ListFiles(string root)
    {
        var result = new List<string>();
        var folders = new Stack<string>();
        folders.Push(root);

        while (folders.Count > 0 && result.Count < 5000)
        {
            string folder = folders.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    if (!SecretFiles.Contains(Path.GetFileName(file))) result.Add(file);
                }

                foreach (string sub in Directory.EnumerateDirectories(folder))
                {
                    if (!IgnoredFolders.Contains(Path.GetFileName(sub))) folders.Push(sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // dossier illisible : ignoré
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static bool IsText(string path) =>
        TextExtensions.Contains(Path.GetExtension(path)) || TextExtensions.Contains(Path.GetFileName(path));

    private static long SafeSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
