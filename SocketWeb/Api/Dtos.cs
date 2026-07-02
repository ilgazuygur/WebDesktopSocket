using SocketWeb.Data;

namespace SocketWeb.Api;

// The shapes returned/accepted by /api/sessions. Kept separate from the
// EF Core entities in SocketWeb.Data on purpose: the API contract
// (what a browser sees) shouldn't change just because the database
// model changes, and entities shouldn't leak EF Core navigation
// properties (e.g. ChatMessage.Session) into JSON responses.

// Used in the session list (GET /api/sessions) - no messages, so the
// sidebar can load quickly even with a lot of history.
public record ChatSessionSummaryDto(Guid Id, string Title, DateTime CreatedAt, DateTime UpdatedAt);

public record ChatMessageDto(Guid Id, string Role, string Content, DateTime CreatedAt, int Sequence);

// Used when opening a single session (GET /api/sessions/{id}) - includes
// every message, in order.
public record ChatSessionDetailDto(Guid Id, string Title, DateTime CreatedAt, DateTime UpdatedAt, List<ChatMessageDto> Messages);

// Body for POST /api/sessions. Title is optional - if omitted, the
// session starts untitled and gets auto-titled from the first user
// message (see SessionTitleGenerator).
public record CreateSessionRequest(string? Title);

// Body for PUT /api/sessions/{id}.
public record RenameSessionRequest(string Title);

internal static class ChatDtoMapping
{
    public static ChatSessionSummaryDto ToSummaryDto(this ChatSession session) =>
        new(session.Id, session.Title, session.CreatedAt, session.UpdatedAt);

    public static ChatMessageDto ToDto(this ChatMessage message) =>
        new(message.Id, message.Role, message.Content, message.CreatedAt, message.Sequence);

    public static ChatSessionDetailDto ToDetailDto(this ChatSession session) =>
        new(session.Id, session.Title, session.CreatedAt, session.UpdatedAt,
            session.Messages.Select(m => m.ToDto()).ToList());
}
