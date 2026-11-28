using KeycesCode.Core.Models;

namespace KeycesCode.Core.Abstractions;

public interface IAssistantService
{
    // Le service d'IA est-il joignable ?
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    // Modèles disponibles (ex. gemini-flash-latest)
    Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default);

    // Envoie la conversation et renvoie la réponse morceau par morceau
    IAsyncEnumerable<string> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);
}
