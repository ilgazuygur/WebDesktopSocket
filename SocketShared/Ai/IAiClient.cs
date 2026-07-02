using SocketShared.Protocol;

namespace SocketShared.Ai;

// The one thing the rest of the app needs from "an AI provider": given a
// conversation so far, get back the assistant's next reply. Everything
// provider-specific (the exact HTTP request/response shape, auth header,
// base URL) lives behind this interface in a concrete implementation like
// OpenAiCompatibleClient, so swapping providers later means writing a new
// implementation of this interface, not rewriting the app.
public interface IAiClient
{
    // messages is the full conversation history to send, oldest first,
    // ending with the newest user message. Returns the assistant's reply
    // text. Throws a subclass of AiClientException on any failure.
    Task<string> CompleteAsync(IReadOnlyList<ConversationTurn> messages, CancellationToken cancellationToken = default);
}
