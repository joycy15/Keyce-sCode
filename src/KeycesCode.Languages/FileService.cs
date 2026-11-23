using System.Text;
using System.IO;

namespace KeycesCode.Languages;

public sealed class FileService
{
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(
            path,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            cancellationToken);

    public void CreateEmptyFile(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    public void CreateDirectory(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new IOException($"Un fichier ou dossier nommé « {Path.GetFileName(path)} » existe déjà.");
        }

        Directory.CreateDirectory(path);
    }

    public async Task SaveAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Le chemin doit désigner un fichier.", nameof(path));
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
