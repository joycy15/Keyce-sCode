using KeycesCode.Core.Models;
using KeycesCode.Settings;
using KeycesCode.Settings.KeyBindings;

namespace KeycesCode.Tests.Parametres;

public class KeyBindingServiceTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "KeycesCodeKeyTests", Guid.NewGuid().ToString());
    private readonly SettingsService _settings;
    private readonly KeyBindingService _service;
    private int _saveExecutions;

    public KeyBindingServiceTests()
    {
        _settings = new SettingsService(_folder);
        _service = new KeyBindingService(_settings);

        _service.Register(new CommandInfo("test.save", "Enregistrer", "Ctrl+S"), () => _saveExecutions++);
        _service.Register(new CommandInfo("test.open", "Ouvrir", "Ctrl+O"), () => { });
    }

    [Fact]
    public void GetShortcut_SansPersonnalisation_RenvoieLeDefaut()
    {
        Assert.Equal("Ctrl+S", _service.GetShortcut("test.save"));
        Assert.False(_service.IsCustomized("test.save"));
    }

    [Fact]
    public async Task SetShortcut_PuisGet_RenvoieLeNouveauEtLEnregistre()
    {
        await _service.SetShortcutAsync("test.save", "Ctrl+Shift+S");

        Assert.Equal("Ctrl+Shift+S", _service.GetShortcut("test.save"));
        Assert.True(_service.IsCustomized("test.save"));

        // Le choix est bien écrit dans settings.json
        var relu = new SettingsService(_folder);
        await relu.LoadAsync();
        Assert.Equal("Ctrl+Shift+S", relu.Current.KeyBindings["test.save"]);
    }

    [Fact]
    public async Task SetShortcut_DejaUtilise_LeveUneErreur()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SetShortcutAsync("test.save", "Ctrl+O"));
    }

    [Fact]
    public async Task Reset_RevientAuRaccourciParDefaut()
    {
        await _service.SetShortcutAsync("test.save", "Ctrl+Shift+S");

        await _service.ResetAsync("test.save");

        Assert.Equal("Ctrl+S", _service.GetShortcut("test.save"));
    }

    [Fact]
    public void TryExecute_LanceLaBonneCommande()
    {
        Assert.True(_service.TryExecute("Ctrl+S"));
        Assert.Equal(1, _saveExecutions);

        Assert.False(_service.TryExecute("Ctrl+Q"));   // aucun raccourci de ce nom
    }

    [Theory]
    [InlineData(true,  false, false, "S",  "Ctrl+S")]
    [InlineData(true,  true,  false, "G",  "Ctrl+Shift+G")]
    [InlineData(true,  false, true,  "P",  "Ctrl+Alt+P")]
    [InlineData(false, false, false, "F5", "F5")]
    [InlineData(false, false, false, "A",  null)]
    [InlineData(false, true,  false, "A",  null)]
    public void ShortcutText_Create(bool ctrl, bool shift, bool alt, string key, string? attendu)
    {
        Assert.Equal(attendu, ShortcutText.Create(ctrl, shift, alt, key));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
