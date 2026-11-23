using System.Net;
using System.Net.Http;
using System.Text;
using KeycesCode.Assistant;
using KeycesCode.Core.Models;

namespace KeycesCode.Tests.IA;

// Faux serveur : renvoie la réponse qu'on veut et garde le JSON reçu
internal sealed class FakeOllama(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
        return respond(request);
    }
}

public class OllamaAssistantServiceTests
{
    private static OllamaAssistantService Create(FakeOllama fake) =>
        new(new HttpClient(fake) { BaseAddress = new Uri("http://localhost:11434/") });

    private static HttpResponseMessage Reply(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static FakeOllama NotRunning() =>
        new(_ => throw new HttpRequestException("Connexion refusée"));

    private static readonly ChatMessage[] Question = [new(ChatRole.User, "Salut")];

    [Fact]
    public async Task IsAvailable_renvoie_true_quand_Ollama_repond()
    {
        var service = Create(new FakeOllama(_ => Reply("{\"models\":[]}")));

        Assert.True(await service.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_renvoie_false_quand_Ollama_est_eteint()
    {
        var service = Create(NotRunning());

        Assert.False(await service.IsAvailableAsync());
    }

    [Fact]
    public async Task GetModels_lit_les_noms_des_modeles()
    {
        var json = "{\"models\":[{\"name\":\"qwen2.5-coder:1.5b\"},{\"name\":\"llama3:latest\"}]}";
        var service = Create(new FakeOllama(_ => Reply(json)));

        var models = await service.GetModelsAsync();

        Assert.Equal(["qwen2.5-coder:1.5b", "llama3:latest"], models);
    }

    [Fact]
    public async Task GetModels_leve_AssistantException_quand_Ollama_est_eteint()
    {
        var service = Create(NotRunning());

        await Assert.ThrowsAsync<AssistantException>(() => service.GetModelsAsync());
    }

    [Fact]
    public async Task StreamChat_renvoie_les_morceaux_dans_l_ordre()
    {
        // Ollama envoie une ligne JSON par morceau
        var lines =
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Bon\"},\"done\":false}\n" +
            "{\"message\":{\"role\":\"assistant\",\"content\":\"jour\"},\"done\":false}\n" +
            "{\"done\":true}\n";
        var service = Create(new FakeOllama(_ => Reply(lines)));

        var parts = new List<string>();
        await foreach (var part in service.StreamChatAsync("qwen", Question))
            parts.Add(part);

        Assert.Equal(["Bon", "jour"], parts);
    }

    [Fact]
    public async Task StreamChat_envoie_le_bon_JSON()
    {
        var fake = new FakeOllama(_ => Reply("{\"done\":true}\n"));
        var service = Create(fake);
        ChatMessage[] messages = [new(ChatRole.System, "Consignes"), new(ChatRole.User, "Salut")];

        await foreach (var _ in service.StreamChatAsync("qwen", messages)) { }

        Assert.NotNull(fake.LastBody);
        Assert.Contains("\"model\":\"qwen\"", fake.LastBody);
        Assert.Contains("\"role\":\"system\"", fake.LastBody);
        Assert.Contains("\"role\":\"user\"", fake.LastBody);
        Assert.Contains("\"stream\":true", fake.LastBody);
    }

    [Fact]
    public async Task StreamChat_transmet_l_erreur_d_Ollama()
    {
        var error = "{\"error\":\"model 'inconnu' not found\"}";
        var service = Create(new FakeOllama(_ => Reply(error, HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<AssistantException>(async () =>
        {
            await foreach (var _ in service.StreamChatAsync("inconnu", Question)) { }
        });

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task StreamChat_refuse_un_modele_vide()
    {
        var service = Create(new FakeOllama(_ => Reply("{\"done\":true}\n")));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in service.StreamChatAsync("  ", Question)) { }
        });
    }
}
