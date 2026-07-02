namespace SocketWeb.Data;

// One message inside a ChatSession - either what the user typed, or what
// the AI replied. This is a database entity (persisted via ChatDbContext),
// which is intentionally a different type from SocketShared.Protocol's
// wire messages: this one describes a row in the ChatMessages table, the
// protocol types describe what travels over the WebSocket.
public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    // EF Core navigation property back to the owning session.
    public ChatSession? Session { get; set; }

    // "user", "assistant" or "system" - see MessageRoles for the constants.
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Monotonically increasing per session (1, 2, 3, ...), assigned by
    // ChatRepository.AddMessageAsync. Messages are always loaded ordered
    // by this value, so conversation order never depends on CreatedAt
    // clock precision/skew.
    public int Sequence { get; set; }
}
