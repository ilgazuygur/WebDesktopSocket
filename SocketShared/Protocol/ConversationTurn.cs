namespace SocketShared.Protocol;

// One turn of a conversation, in the shape AI chat APIs expect:
// Role is "user", "assistant" or "system"; Content is the text.
// A SocketMessage of type AiRequest carries a list of these so the
// desktop app can send the AI the full context of a session, not just
// the newest message.
public class ConversationTurn
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
