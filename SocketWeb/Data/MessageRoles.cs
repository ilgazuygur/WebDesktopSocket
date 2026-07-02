namespace SocketWeb.Data;

// The only three valid values for ChatMessage.Role, as plain string
// constants (matching what AI chat APIs expect) instead of an enum, so a
// ChatMessage.Role can be sent straight to SocketShared.Protocol.
// ConversationTurn.Role / the AI API without any translation step.
public static class MessageRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
}
