using KeycesCode.Core.Models;
using KeycesCode.Settings;

namespace KeycesCode.Tests.Parametres;

public class SettingsServiceTests : IDisposable
{
    // Un dossier temporaire unique pour chaque test : tes vrais paramètres ne sont jamais touchés
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "KeycesCodeTests", Guid.NewGuid().ToString());

    [Fact]
    public async Task Load_SansFichier_DonneLesValeursParDefaut()
    {
        var service = new SettingsService(_folder);

        await service.LoadAsync();

        Assert.Equal("dark", service.Current.ThemeId);
    }

    [Fact]
    public async Task Save_PuisLoad_RetrouveLesMemesValeurs()
    {
        var service = new SettingsService(_folder);
        await service.SaveAsync(new AppSettings { ThemeId = "light", FontSize = 18 });

        var autreService = new SettingsService(_folder);   // simule un redémarrage de l'IDE
        await autreService.LoadAsync();

        Assert.Equal("light", autreService.Current.ThemeId);
        Assert.Equal(18, autreService.Current.FontSize);
    }

    [Fact]
    public async Task Save_DeclencheSettingsChanged()
    {
        var service = new SettingsService(_folder);
        AppSettings? recu = null;
        service.SettingsChanged += (_, s) => recu = s;

        await service.SaveAsync(service.Current with { FontSize = 20 });

        Assert.NotNull(recu);
        Assert.Equal(20, recu!.FontSize);
    }

    // Exécuté après chaque test : supprime le dossier temporaire
    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}