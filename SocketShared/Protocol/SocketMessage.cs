namespace SocketShared.Protocol;

// The one envelope every WebSocket message uses. Both SocketWeb and
// SocketDesktop serialize/deserialize this exact class, the same way
// SocketShared.ChatMessage worked for the original demo - except this
// shape carries enough information (Type, SessionId, RequestId, Role)
// for the server to route messages correctly instead of just
// broadcasting everything to everyone.
//
// Not every field is used by every message Type - see the comments on
// MessageType for which fields matter for which message.
public class SocketMessage
{
    public MessageType Type { get; set; }

    // Which kind of client this is. Only set on ClientHello.
    public ClientRole? Role { get; set; }

    // Which chat session this message belongs to (UserPrompt, AiRequest,
    // AiResponse, Status, Error).
    public string? SessionId { get; set; }

    // Correlates an AiRequest with its AiResponse (or Error), and lets
    // the server route the reply back to the browser that asked, and
    // ignore any duplicate/late replies for a request it already handled.
    public string? RequestId { get; set; }

    // Identifies which browser connection sent a UserPrompt, so the
    // server knows which socket to route the eventual AiResponse to.
    // Assigned by the server on ClientHello / HelloAck.
    public string? ConnectionId { get; set; }

    // The actual text: the user's prompt, the AI's reply, or a status
    // message like "AI is thinking".
    public string? Content { get; set; }

    // The role the Content should be recorded under when persisted
    // ("user" / "assistant" / "system"). Used with UserPrompt/AiResponse.
    public string? MessageRole { get; set; }

    // Prior conversation turns for this session, sent with AiRequest so
    // the AI has context instead of only seeing the newest message.
    public List<ConversationTurn>? History { get; set; }

    // Human-readable error detail, set on Type == Error.
    public string? Error { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
