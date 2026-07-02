namespace SocketWeb.Data;

// A single conversation, similar to one "chat" in ChatGPT's sidebar.
// Every ChatMessage belongs to exactly one ChatSession, via SessionId.
public class ChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Shown in the sidebar. Auto-generated from the first user message
    // if the caller doesn't supply one - see SessionTitleGenerator.
    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Bumped every time a message is added, so the sidebar can show the
    // most recently active sessions first.
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // EF Core navigation property - the messages in this session.
    public List<ChatMessage> Messages { get; set; } = new();
}
