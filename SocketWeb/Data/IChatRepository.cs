namespace SocketWeb.Data;

// Everything the rest of the app needs to read/write chat sessions and
// messages, behind one interface. The /api/sessions endpoints (CRUD) and
// the WebSocket AI flow (saving prompts/replies, loading history) both
// go through this, instead of touching ChatDbContext directly.
public interface IChatRepository
{
    // Newest-updated first, without their messages loaded - just enough
    // for the sidebar list.
    Task<List<ChatSession>> GetSessionsAsync(CancellationToken cancellationToken = default);

    // The session plus all of its messages, ordered by Sequence. Null if
    // no session with that id exists.
    Task<ChatSession?> GetSessionWithMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default);

    // Just the messages for a session, ordered by Sequence.
    Task<List<ChatMessage>> GetMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<ChatSession> CreateSessionAsync(string? title, CancellationToken cancellationToken = default);

    // False if no session with that id exists.
    Task<bool> RenameSessionAsync(Guid sessionId, string title, CancellationToken cancellationToken = default);

    // False if no session with that id exists. Cascade-deletes its messages.
    Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    // Appends a message to a session, assigns it the next Sequence
    // number, bumps the session's UpdatedAt, and auto-titles the session
    // from this message if it doesn't have a title yet. Returns null if
    // no session with that id exists.
    Task<ChatMessage?> AddMessageAsync(Guid sessionId, string role, string content, CancellationToken cancellationToken = default);
}
