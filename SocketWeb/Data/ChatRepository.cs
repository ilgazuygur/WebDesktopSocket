using Microsoft.EntityFrameworkCore;

namespace SocketWeb.Data;

public class ChatRepository : IChatRepository
{
    private readonly ChatDbContext _db;

    public ChatRepository(ChatDbContext db)
    {
        _db = db;
    }

    public async Task<List<ChatSession>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        return await _db.ChatSessions
            .AsNoTracking()
            .OrderByDescending(session => session.UpdatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<ChatSession?> GetSessionWithMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return await _db.ChatSessions
            .Include(session => session.Messages.OrderBy(message => message.Sequence))
            .AsSplitQuery()
            .FirstOrDefaultAsync(session => session.Id == sessionId, cancellationToken);
    }

    public async Task<List<ChatMessage>> GetMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return await _db.ChatMessages
            .AsNoTracking()
            .Where(message => message.SessionId == sessionId)
            .OrderBy(message => message.Sequence)
            .ToListAsync(cancellationToken);
    }

    public async Task<ChatSession> CreateSessionAsync(string? title, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var session = new ChatSession
        {
            Title = title ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.ChatSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<bool> RenameSessionAsync(Guid sessionId, string title, CancellationToken cancellationToken = default)
    {
        var session = await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return false;
        }

        session.Title = title;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        // Messages are loaded here (not just the session row) so EF Core's
        // change tracker knows to cascade the delete client-side too. On
        // MySQL the ON DELETE CASCADE foreign key would handle this on its
        // own, but loading them keeps behavior identical for the EF Core
        // InMemory provider used in tests, which doesn't enforce
        // database-level foreign keys.
        var session = await _db.ChatSessions
            .Include(s => s.Messages)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return false;
        }

        _db.ChatSessions.Remove(session);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ChatMessage?> AddMessageAsync(Guid sessionId, string role, string content, CancellationToken cancellationToken = default)
    {
        var session = await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var lastSequence = await _db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .Select(m => (int?)m.Sequence)
            .MaxAsync(cancellationToken) ?? 0;

        var now = DateTime.UtcNow;
        var message = new ChatMessage
        {
            SessionId = sessionId,
            Role = role,
            Content = content,
            CreatedAt = now,
            Sequence = lastSequence + 1
        };

        _db.ChatMessages.Add(message);

        session.UpdatedAt = now;
        if (string.IsNullOrWhiteSpace(session.Title) && string.Equals(role, MessageRoles.User, StringComparison.OrdinalIgnoreCase))
        {
            session.Title = SessionTitleGenerator.FromFirstMessage(content);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return message;
    }
}
