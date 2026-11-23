using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using KeycesCode.Core.Abstractions;
using KeycesCode.Core.Models;

namespace KeycesCode.Assistant;

// Parle à Ollama sur http://localhost:11434 (tout reste sur le PC)
public sealed class OllamaAssistantService : IAssistantService
{
    private const string NotRunning =
        "Ollama ne répond pas. Vérifie qu'il est lancé (icône dans la barre des tâches).";

    // Web = noms JSON en camelCase ("model", "messages"...) comme l'API d'Ollama
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public OllamaAssistantService()
        : this(new HttpClient
        {
            BaseAddress = new Uri("http://localhost:11434/"),
            Timeout = Timeout.InfiniteTimeSpan // une réponse peut être longue ; on coupe avec le bouton Arrêter
        })
    {
    }

    // Constructeur pour les tests (on pourra donner un faux serveur)
    public OllamaAssistantService(HttpClient http) => _http = http;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3)); // on n'attend pas plus de 3 s

        try
        {
            using var response = await _http.GetAsync("api/tags", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // délai de 3 s dépassé
        }
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tags = await _http.GetFromJsonAsync<TagsResponse>("api/tags", Json, cancellationToken);
            return tags?.Models?.Select(m => m.Name).ToList() ?? [];
        }
        catch (HttpRequestException)
        {
            throw new AssistantException(NotRunning);
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Aucun modèle n'est choisi.", nameof(model));

        var body = new ChatRequest(
            model,
            messages.Select(m => new OllamaMessage(ToRole(m.Role), m.Content)).ToList(),
            Stream: true);

        using var response = await SendAsync(body, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // Ollama renvoie une ligne JSON par morceau de réponse
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0) continue;

            var chunk = JsonSerializer.Deserialize<ChatChunk>(line, Json);
            if (chunk is null) continue;

            if (!string.IsNullOrEmpty(chunk.Error))
                throw new AssistantException(chunk.Error);

            if (!string.IsNullOrEmpty(chunk.Message?.Content))
                yield return chunk.Message.Content;

            if (chunk.Done) yield break;
        }
    }

    // Envoie la requête ; transforme les erreurs réseau en messages compréhensibles
    private async Task<HttpResponseMessage> SendAsync(ChatRequest body, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
            {
                Content = JsonContent.Create(body, options: Json)
            };
            // ResponseHeadersRead = on commence à lire sans attendre la fin de la réponse
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new AssistantException(NotRunning);
        }

        if (response.IsSuccessStatusCode) return response;

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = response.StatusCode;
        response.Dispose();
        throw new AssistantException(ReadError(text, status));
    }

    private static string ReadError(string text, HttpStatusCode status)
    {
        try
        {
            var error = JsonSerializer.Deserialize<ChatChunk>(text, Json)?.Error;
            if (!string.IsNullOrWhiteSpace(error)) return $"Ollama : {error}";
        }
        catch (JsonException)
        {
        }
        return $"Ollama a répondu avec l'erreur {(int)status}.";
    }

    private static string ToRole(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.Assistant => "assistant",
        _ => "user"
    };
}

// Formes des JSON échangés avec Ollama
internal sealed record ChatRequest(string Model, IReadOnlyList<OllamaMessage> Messages, bool Stream);
internal sealed record OllamaMessage(string Role, string Content);
internal sealed record ChatChunk(OllamaMessage? Message, bool Done, string? Error);
internal sealed record TagsResponse(IReadOnlyList<ModelInfo>? Models);
internal sealed record ModelInfo(string Name);
