using System.IO;
using System.Text.Json;
using KeycesCode.Core.Abstractions;
using KeycesCode.Core.Models;

namespace KeycesCode.Settings;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public SettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeycesCode"))
    { }

    public SettingsService(string folder)
    {
        _filePath = Path.Combine(folder, "settings.json");
    }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task LoadAsync()
    {
        if (!File.Exists(_filePath))
        {
            Current = new AppSettings();
            return;
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            Current = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions)
                      ?? new AppSettings();
        }
        catch (JsonException)
        {
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        var tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
        }
        File.Move(tempPath, _filePath, overwrite: true);

        Current = settings;
        SettingsChanged?.Invoke(this, settings);
    }
}