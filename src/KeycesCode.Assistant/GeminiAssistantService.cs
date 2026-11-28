using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeycesCode.Core.Abstractions;
using KeycesCode.Core.Models;

namespace KeycesCode.Assistant;

// Parle à l'API Gemini de Google (https://generativelanguage.googleapis.com).
// Il faut Internet et une clé API gratuite : https://aistudio.google.com/apikey
public sealed class GeminiAssistantService : IAssistantService
{
    // Toujours le dernier modèle Flash de Google (rapide, bon en code, offre gratuite)
    public const string DefaultModel = "gemini-flash-latest";

    private const string NoKey =
        "Clé API Gemini manquante. Ajoute-la dans Paramètres → Assistant IA (gratuite sur aistudio.google.com/apikey).";
    // Réflexion minimale : les modèles Flash « réfléchissent » longtemps avant d'écrire si on ne le précise pas.
    // « minimal » = la réponse commence presque tout de suite. Si le modèle refuse ce niveau, on essaie « low ».
    public const string ThinkingLevel = "minimal";

    // Si aucun mot n'arrive dans ce délai, on abandonne et on passe au modèle de secours (plus rapide)
    public TimeSpan FirstAnswerTimeout { get; init; } = TimeSpan.FromSeconds(30);

    // Modèle de secours quand le modèle principal est surchargé (erreur 503) ou a atteint sa limite (429)
    public const string BackupModel = "gemini-flash-lite-latest";

    // Attente avant de réessayer après une erreur passagère (1 s, puis 2 s...). Zéro dans les tests.
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    private const string Offline =
        "Impossible de joindre Gemini. Vérifie ta connexion Internet.";

    // Web = noms JSON en camelCase ("contents", "systemInstruction"...) comme l'API Gemini
    // WhenWritingNull : les champs vides (pas de consignes, par exemple) ne sont pas envoyés
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly Func<string> _getApiKey;

    public GeminiAssistantService(Func<string> getApiKey)
        : this(getApiKey, new HttpClient
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/"),
            Timeout = Timeout.InfiniteTimeSpan   // une réponse peut être longue ; on coupe avec le bouton Arrêter
        })
    {
    }

    // Constructeur pour les tests (on donne un faux serveur)
    public GeminiAssistantService(Func<string> getApiKey, HttpClient http)
    {
        _getApiKey = getApiKey;
        _http = http;
    }

    private string ApiKey => _getApiKey()?.Trim() ?? "";

    // Un modèle qui n'est pas Gemini (ancien réglage Ollama, par exemple) est remplacé par le modèle par défaut
    public static string ResolveModel(string model) =>
        model.Trim().StartsWith("gemini", StringComparison.OrdinalIgnoreCase) ? model.Trim() : DefaultModel;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (ApiKey.Length == 0) return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            using var request = Request(HttpMethod.Get, "v1beta/models?pageSize=1");
            using var response = await _http.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;   // délai dépassé
        }
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        if (ApiKey.Length == 0) throw new AssistantException(NoKey);

        try
        {
            using var request = Request(HttpMethod.Get, "v1beta/models?pageSize=200");
            using var response = await _http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new AssistantException(ReadError(body, response.StatusCode));

            var list = JsonSerializer.Deserialize<GeminiModelList>(body, Json);
            return list?.Models?
                .Where(m => m.SupportedGenerationMethods?.Contains("generateContent") == true)
                .Select(m => m.Name.Replace("models/", "", StringComparison.Ordinal))
                .ToList() ?? [];
        }
        catch (HttpRequestException)
        {
            throw new AssistantException(Offline);
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (ApiKey.Length == 0) throw new AssistantException(NoKey);
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Aucun modèle n'est choisi.", nameof(model));

        // Les consignes (System) vont dans systemInstruction ; la discussion dans contents
        string system = string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        var contents = messages
            .Where(m => m.Role != ChatRole.System)
            .Select(m => new GeminiContent(m.Role == ChatRole.Assistant ? "model" : "user", [new GeminiPart(m.Content)]))
            .ToList();

        var body = new GeminiRequest(
            contents,
            system.Length > 0 ? new GeminiContent(null, [new GeminiPart(system)]) : null,
            new GeminiGenerationConfig(new GeminiThinkingConfig(ThinkingLevel)));

        string first = ResolveModel(model);
        bool tooSlow = false;

        // Modèle choisi ; s'il ne commence pas à répondre à temps, on passe au modèle de secours
        await using (var answer = StreamModelAsync(first, body, cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await answer.MoveNextAsync();
                }
                catch (TimeoutException) when (!IsLite(first))
                {
                    tooSlow = true;
                    break;
                }

                if (!hasNext) break;
                yield return answer.Current;
            }
        }

        if (!tooSlow) yield break;

        await foreach (string piece in StreamModelAsync(BackupModel, body, cancellationToken))
            yield return piece;
    }

    // Les modèles « Lite » répondent vite sans réglage de réflexion
    private static bool IsLite(string model) => model.Contains("lite", StringComparison.OrdinalIgnoreCase);

    // Une réponse d'un modèle, morceau par morceau. TimeoutException si le premier mot n'arrive pas à temps.
    private async IAsyncEnumerable<string> StreamModelAsync(
        string model, GeminiRequest body, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var firstWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        firstWait.CancelAfter(FirstAnswerTimeout);

        HttpResponseMessage response;
        try
        {
            response = await SendWithRetryAsync(model, body, firstWait.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }

        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            bool started = false;

            // Réponse en « Server-Sent Events » : une ligne « data: {json} » par morceau
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(firstWait.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException();
                }

                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var chunk = JsonSerializer.Deserialize<GeminiResponse>(line[5..].Trim(), Json);
                if (chunk?.Error is { } error)
                    throw new AssistantException($"Gemini : {error.Message}");

                var candidate = chunk?.Candidates?.FirstOrDefault();
                foreach (var part in candidate?.Content?.Parts ?? [])
                {
                    // Les « pensées » internes du modèle ne sont pas affichées
                    if (part.Thought == true || string.IsNullOrEmpty(part.Text)) continue;

                    if (!started)
                    {
                        started = true;
                        firstWait.CancelAfter(Timeout.InfiniteTimeSpan);   // la réponse a commencé : plus de limite
                    }
                    yield return part.Text;
                }

                if (candidate?.FinishReason is "SAFETY" or "RECITATION" or "BLOCKLIST")
                    throw new AssistantException("Gemini a interrompu sa réponse (filtre de sécurité de Google).");
            }
        }
    }

    // Envoie la question. Erreur passagère (surcharge 503, limite 429, coupure réseau) : on réessaie
    // deux fois, puis avec le modèle de secours. On peut ainsi poser autant de questions qu'on veut.
    private async Task<HttpResponseMessage> SendWithRetryAsync(string model, GeminiRequest body, CancellationToken cancellationToken)
    {
        string[] models = string.Equals(model, BackupModel, StringComparison.OrdinalIgnoreCase)
            ? [model]
            : [model, BackupModel];
        string lastError = Offline;

        foreach (string current in models)
        {
            // Modèle « Lite » : déjà rapide, sans réglage de réflexion
            GeminiRequest request = IsLite(current) ? body with { GenerationConfig = null } : body;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var (response, status, error) = await TrySendAsync(current, request, cancellationToken);
                if (response is not null) return response;

                // Le modèle refuse ce niveau de réflexion : « minimal » → « low » → aucun réglage
                if (status == 400 && request.GenerationConfig is not null &&
                    error.Contains("thinking", StringComparison.OrdinalIgnoreCase))
                {
                    request = request.GenerationConfig.ThinkingConfig?.ThinkingLevel == "minimal"
                        ? request with { GenerationConfig = new GeminiGenerationConfig(new GeminiThinkingConfig("low")) }
                        : request with { GenerationConfig = null };
                    attempt--;
                    continue;
                }

                lastError = error;
                bool temporary = status is 0 or 408 or 429 or >= 500;
                if (!temporary) throw new AssistantException(error);

                if (attempt < 2) await Task.Delay(RetryDelay * (attempt + 1), cancellationToken);
            }
        }

        throw new AssistantException(lastError);
    }

    // Une tentative : la réponse si tout va bien, sinon le code d'erreur (0 = pas de réseau) et le message
    private async Task<(HttpResponseMessage? Response, int Status, string Error)> TrySendAsync(
        string model, GeminiRequest body, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = Request(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse");
            request.Content = JsonContent.Create(body, options: Json);
            // ResponseHeadersRead = on commence à lire sans attendre la fin de la réponse
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return (null, 0, Offline);
        }

        if (response.IsSuccessStatusCode) return (response, (int)response.StatusCode, "");

        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = response.StatusCode;
        response.Dispose();
        return (null, (int)status, ReadError(text, status));
    }

    // La clé est envoyée dans l'en-tête x-goog-api-key (jamais dans l'adresse)
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("x-goog-api-key", ApiKey);
        return request;
    }

    // Messages d'erreur compréhensibles selon le code renvoyé par Google
    private static string ReadError(string text, HttpStatusCode status)
    {
        string? detail = null;
        try
        {
            detail = JsonSerializer.Deserialize<GeminiResponse>(text, Json)?.Error?.Message;
        }
        catch (JsonException)
        {
        }

        return (int)status switch
        {
            400 when detail?.Contains("API key", StringComparison.OrdinalIgnoreCase) == true
                => "Clé API Gemini invalide. Vérifie-la dans Paramètres → Assistant IA.",
            401 or 403 => "Clé API Gemini refusée. Vérifie-la dans Paramètres → Assistant IA.",
            404 => "Modèle Gemini introuvable. Vérifie son nom dans Paramètres → Assistant IA (par défaut : gemini-flash-latest).",
            429 => "Limite de l'offre gratuite Gemini atteinte. Attends une minute puis réessaie.",
            >= 500 => "Gemini est momentanément indisponible. Réessaie dans un instant.",
            _ => detail is not null ? $"Gemini : {detail}" : $"Gemini a répondu avec l'erreur {(int)status}."
        };
    }
}

// Formes des JSON échangés avec l'API Gemini
internal sealed record GeminiRequest(IReadOnlyList<GeminiContent> Contents, GeminiContent? SystemInstruction,
                                     GeminiGenerationConfig? GenerationConfig = null);
internal sealed record GeminiGenerationConfig(GeminiThinkingConfig? ThinkingConfig);
internal sealed record GeminiThinkingConfig(string? ThinkingLevel);
internal sealed record GeminiContent(string? Role, IReadOnlyList<GeminiPart> Parts);
internal sealed record GeminiPart(string? Text, bool? Thought = null);
internal sealed record GeminiResponse(IReadOnlyList<GeminiCandidate>? Candidates, GeminiError? Error);
internal sealed record GeminiCandidate(GeminiContent? Content, string? FinishReason);
internal sealed record GeminiError(int Code, string? Message, string? Status);
internal sealed record GeminiModelList(IReadOnlyList<GeminiModelInfo>? Models);
internal sealed record GeminiModelInfo(string Name, IReadOnlyList<string>? SupportedGenerationMethods);
