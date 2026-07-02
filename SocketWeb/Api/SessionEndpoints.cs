using SocketWeb.Data;

namespace SocketWeb.Api;

// The REST side of chat sessions: create/list/load/rename/delete. This is
// plain database CRUD, so it's ordinary HTTP endpoints rather than
// WebSocket messages - the WebSocket side (Program.cs's /ws endpoint) is
// reserved for the real-time AI request/response flow.
public static class SessionEndpoints
{
    private const int MaxTitleLength = 200;

    public static void MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/sessions");

        // List sessions for the sidebar, most recently active first.
        group.MapGet("", async (IChatRepository repo, CancellationToken cancellationToken) =>
        {
            var sessions = await repo.GetSessionsAsync(cancellationToken);
            return Results.Ok(sessions.Select(s => s.ToSummaryDto()));
        });

        // Create a new (optionally titled) session - the "New chat" button.
        group.MapPost("", async (CreateSessionRequest? request, IChatRepository repo, CancellationToken cancellationToken) =>
        {
            var title = request?.Title?.Trim();

            if (title is { Length: > MaxTitleLength })
            {
                return Results.BadRequest(new { error = $"Title must be {MaxTitleLength} characters or fewer." });
            }

            var session = await repo.CreateSessionAsync(string.IsNullOrWhiteSpace(title) ? null : title, cancellationToken);
            return Results.Created($"/api/sessions/{session.Id}", session.ToSummaryDto());
        });

        // Load one session with all of its messages, in order - used
        // when opening a previous session from the sidebar.
        group.MapGet("/{id:guid}", async (Guid id, IChatRepository repo, CancellationToken cancellationToken) =>
        {
            var session = await repo.GetSessionWithMessagesAsync(id, cancellationToken);
            return session is null ? Results.NotFound() : Results.Ok(session.ToDetailDto());
        });

        // Just the messages, in case a caller wants to refresh a session's
        // messages without re-fetching its title/timestamps.
        group.MapGet("/{id:guid}/messages", async (Guid id, IChatRepository repo, CancellationToken cancellationToken) =>
        {
            var session = await repo.GetSessionWithMessagesAsync(id, cancellationToken);
            return session is null
                ? Results.NotFound()
                : Results.Ok(session.Messages.Select(m => m.ToDto()));
        });

        // Rename a session.
        group.MapPut("/{id:guid}", async (Guid id, RenameSessionRequest request, IChatRepository repo, CancellationToken cancellationToken) =>
        {
            var title = request.Title?.Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                return Results.BadRequest(new { error = "Title is required." });
            }

            if (title.Length > MaxTitleLength)
            {
                return Results.BadRequest(new { error = $"Title must be {MaxTitleLength} characters or fewer." });
            }

            var renamed = await repo.RenameSessionAsync(id, title, cancellationToken);
            return renamed ? Results.NoContent() : Results.NotFound();
        });

        // Delete a session and (via cascade) all of its messages. The
        // confirmation prompt itself is a browser-side UI concern, not
        // something this endpoint needs to know about.
        group.MapDelete("/{id:guid}", async (Guid id, IChatRepository repo, CancellationToken cancellationToken) =>
        {
            var deleted = await repo.DeleteSessionAsync(id, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        });
    }
}
