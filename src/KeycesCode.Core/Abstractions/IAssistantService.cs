using KeycesCode.Core.Models;

namespace KeycesCode.Core.Abstractions;

public interface IAssistantService
{
    // Ollama est-il lancé ?
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    // Modèles installés (ex. qwen2.5-coder:1.5b)
    Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default);

    // Envoie la conversation et renvoie la réponse morceau par morceau
    IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);
}
