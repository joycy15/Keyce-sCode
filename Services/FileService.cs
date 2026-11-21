using System.Text;
using System.IO;

namespace Keyce_sCode.Services;

public sealed class FileService
{
    public string ReadAllText(string path) => File.ReadAllText(path);

    public void Save(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
