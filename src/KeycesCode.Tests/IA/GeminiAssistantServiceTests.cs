using System.Net;
using System.Net.Http;
using System.Text;
using KeycesCode.Assistant;
using KeycesCode.Core.Models;

namespace KeycesCode.Tests.IA;

// Faux serveur Gemini : renvoie la réponse qu'on veut et garde la requête reçue
internal sealed class FakeGemini(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
        return respond(request);
    }
}

public class GeminiAssistantServiceTests
{
    private const string Key = "cle-de-test";

    private static GeminiAssistantService Create(FakeGemini fake, string key = Key) =>
        new(() => key, new HttpClient(fake) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") })
        {
            RetryDelay = TimeSpan.Zero   // pas d'attente entre les essais pendant les tests
        };

    private static HttpResponseMessage Reply(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static FakeGemini Offline() => new(_ => throw new HttpRequestException("Pas d'Internet"));

    private static readonly ChatMessage[] Question = [new(ChatRole.User, "Salut")];

    private static async Task<List<string>> ReadAll(IAsyncEnumerable<string> stream)
    {
        var parts = new List<string>();
        await foreach (var part in stream) parts.Add(part);
        return parts;
    }

    [Fact]
    public void ResolveModel_remplace_un_ancien_modele_Ollama()
    {
        Assert.Equal(GeminiAssistantService.DefaultModel, GeminiAssistantService.ResolveModel("qwen2.5-coder:1.5b"));
        Assert.Equal("gemini-2.5-pro", GeminiAssistantService.ResolveModel("gemini-2.5-pro"));
    }

    [Fact]
    public async Task IsAvailable_renvoie_false_sans_cle()
    {
        var service = Create(new FakeGemini(_ => Reply("{}")), key: "");

        Assert.False(await service.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailable_renvoie_false_sans_Internet()
    {
        Assert.False(await Create(Offline()).IsAvailableAsync());
    }

    [Fact]
    public async Task GetModels_garde_les_modeles_de_generation()
    {
        var json = "{\"models\":[" +
                   "{\"name\":\"models/gemini-flash-latest\",\"supportedGenerationMethods\":[\"generateContent\"]}," +
                   "{\"name\":\"models/text-embedding-004\",\"supportedGenerationMethods\":[\"embedContent\"]}]}";
        var service = Create(new FakeGemini(_ => Reply(json)));

        var models = await service.GetModelsAsync();

        Assert.Equal(["gemini-flash-latest"], models);
    }

    [Fact]
    public async Task GetModels_sans_cle_leve_AssistantException()
    {
        var service = Create(new FakeGemini(_ => Reply("{}")), key: " ");

        await Assert.ThrowsAsync<AssistantException>(() => service.GetModelsAsync());
    }

    [Fact]
    public async Task StreamChat_lit_les_morceaux_SSE_dans_l_ordre()
    {
        var sse =
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Bon\"}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"jour\"}]},\"finishReason\":\"STOP\"}]}\n\n";
        var service = Create(new FakeGemini(_ => Reply(sse)));

        var parts = await ReadAll(service.StreamChatAsync("gemini-flash-latest", Question));

        Assert.Equal(["Bon", "jour"], parts);
    }

    [Fact]
    public async Task StreamChat_ignore_les_pensees_du_modele()
    {
        var sse = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"je réfléchis\",\"thought\":true},{\"text\":\"Réponse\"}]}}]}\n\n";
        var service = Create(new FakeGemini(_ => Reply(sse)));

        var parts = await ReadAll(service.StreamChatAsync("gemini-flash-latest", Question));

        Assert.Equal(["Réponse"], parts);
    }

    [Fact]
    public async Task StreamChat_envoie_la_bonne_requete()
    {
        var fake = new FakeGemini(_ => Reply("data: {\"candidates\":[]}\n\n"));
        var service = Create(fake);
        ChatMessage[] messages =
        [
            new(ChatRole.System, "Consignes"),
            new(ChatRole.User, "Question 1"),
            new(ChatRole.Assistant, "Réponse 1"),
            new(ChatRole.User, "Question 2")
        ];

        await ReadAll(service.StreamChatAsync("gemini-flash-latest", messages));

        Assert.NotNull(fake.LastRequest);
        Assert.Contains("models/gemini-flash-latest:streamGenerateContent", fake.LastRequest!.RequestUri!.ToString());
        Assert.Contains("alt=sse", fake.LastRequest.RequestUri.ToString());
        Assert.Equal(Key, fake.LastRequest.Headers.GetValues("x-goog-api-key").Single());

        Assert.NotNull(fake.LastBody);
        Assert.Contains("\"systemInstruction\"", fake.LastBody);
        Assert.Contains("\"role\":\"model\"", fake.LastBody);   // l'assistant s'appelle « model » chez Gemini
        Assert.Contains("\"role\":\"user\"", fake.LastBody);
        Assert.DoesNotContain("\"role\":\"system\"", fake.LastBody);
    }

    [Fact]
    public async Task StreamChat_demande_une_reflexion_courte_pour_repondre_vite()
    {
        var fake = new FakeGemini(_ => Reply("data: {\"candidates\":[]}\n\n"));
        var service = Create(fake);

        await ReadAll(service.StreamChatAsync("gemini-flash-latest", Question));

        Assert.Contains("\"thinkingConfig\":{\"thinkingLevel\":\"minimal\"}", fake.LastBody);
    }

    [Fact]
    public async Task StreamChat_cle_refusee_donne_un_message_clair()
    {
        var error = "{\"error\":{\"code\":403,\"message\":\"Permission denied\",\"status\":\"PERMISSION_DENIED\"}}";
        var service = Create(new FakeGemini(_ => Reply(error, HttpStatusCode.Forbidden)));

        var ex = await Assert.ThrowsAsync<AssistantException>(
            () => ReadAll(service.StreamChatAsync("gemini-flash-latest", Question)));

        Assert.Contains("Clé API Gemini refusée", ex.Message);
    }

    [Fact]
    public async Task StreamChat_limite_gratuite_atteinte()
    {
        var service = Create(new FakeGemini(_ => Reply("{\"error\":{\"code\":429,\"message\":\"quota\"}}", (HttpStatusCode)429)));

        var ex = await Assert.ThrowsAsync<AssistantException>(
            () => ReadAll(service.StreamChatAsync("gemini-flash-latest", Question)));

        Assert.Contains("Limite", ex.Message);
    }

    [Fact]
    public async Task StreamChat_reessaie_puis_utilise_le_modele_de_secours_si_Gemini_est_surcharge()
    {
        var models = new List<string>();
        var fake = new FakeGemini(request =>
        {
            string url = request.RequestUri!.ToString();
            models.Add(url);
            return url.Contains(GeminiAssistantService.BackupModel)
                ? Reply("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"OK\"}]}}]}\n\n")
                : Reply("{\"error\":{\"code\":503,\"message\":\"overloaded\"}}", HttpStatusCode.ServiceUnavailable);
        });

        var parts = await ReadAll(Create(fake).StreamChatAsync("gemini-flash-latest", Question));

        Assert.Equal(["OK"], parts);
        Assert.Equal(3, models.Count(u => u.Contains("gemini-flash-latest:")));   // 3 essais sur le modèle principal
        Assert.Contains(models, u => u.Contains(GeminiAssistantService.BackupModel));
    }

    [Fact]
    public async Task StreamChat_sans_Internet_donne_un_message_clair()
    {
        var ex = await Assert.ThrowsAsync<AssistantException>(
            () => ReadAll(Create(Offline()).StreamChatAsync("gemini-flash-latest", Question)));

        Assert.Contains("Internet", ex.Message);
    }
}
