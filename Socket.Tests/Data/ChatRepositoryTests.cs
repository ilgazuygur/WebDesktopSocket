using Microsoft.EntityFrameworkCore;
using SocketWeb.Data;

namespace Socket.Tests.Data;

// Exercises ChatRepository against the EF Core InMemory provider - fast,
// no real MySQL needed, but still going through the same DbContext/model
// configuration (cascade delete, indexes are relational-only so those
// aren't exercised here, but the *behavior* they're meant to guarantee -
// deterministic ordering, cascading deletes, session isolation - is).
public class ChatRepositoryTests
{
    private static ChatRepository CreateRepository(out ChatDbContext db)
    {
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        db = new ChatDbContext(options);
        return new ChatRepository(db);
    }

    [Fact]
    public async Task CreateSessionAsync_ThenGetSessionsAsync_ReturnsTheNewSession()
    {
        var repo = CreateRepository(out _);

        var created = await repo.CreateSessionAsync("My chat");
        var sessions = await repo.GetSessionsAsync();

        var found = Assert.Single(sessions);
        Assert.Equal(created.Id, found.Id);
        Assert.Equal("My chat", found.Title);
    }

    [Fact]
    public async Task GetSessionsAsync_OrdersByUpdatedAtDescending()
    {
        var repo = CreateRepository(out _);

        var older = await repo.CreateSessionAsync("Older");
        var newer = await repo.CreateSessionAsync("Newer");

        // Force a clearly later UpdatedAt on "older" by adding a message
        // to it after "newer" was created, so it should now sort first.
        await repo.AddMessageAsync(older.Id, MessageRoles.User, "hello again");

        var sessions = await repo.GetSessionsAsync();

        Assert.Equal(older.Id, sessions[0].Id);
        Assert.Equal(newer.Id, sessions[1].Id);
    }

    [Fact]
    public async Task RenameSessionAsync_UpdatesTitle_AndReturnsTrue()
    {
        var repo = CreateRepository(out _);
        var session = await repo.CreateSessionAsync("Old title");

        var result = await repo.RenameSessionAsync(session.Id, "New title");
        var reloaded = await repo.GetSessionWithMessagesAsync(session.Id);

        Assert.True(result);
        Assert.Equal("New title", reloaded!.Title);
    }

    [Fact]
    public async Task RenameSessionAsync_UnknownSession_ReturnsFalse()
    {
        var repo = CreateRepository(out _);

        var result = await repo.RenameSessionAsync(Guid.NewGuid(), "New title");

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteSessionAsync_RemovesSessionAndItsMessages()
    {
        var repo = CreateRepository(out var db);
        var session = await repo.CreateSessionAsync("To delete");
        await repo.AddMessageAsync(session.Id, MessageRoles.User, "hi");
        await repo.AddMessageAsync(session.Id, MessageRoles.Assistant, "hello");

        var result = await repo.DeleteSessionAsync(session.Id);

        Assert.True(result);
        Assert.Null(await repo.GetSessionWithMessagesAsync(session.Id));
        Assert.Empty(await db.ChatMessages.Where(m => m.SessionId == session.Id).ToListAsync());
    }

    [Fact]
    public async Task DeleteSessionAsync_UnknownSession_ReturnsFalse()
    {
        var repo = CreateRepository(out _);

        var result = await repo.DeleteSessionAsync(Guid.NewGuid());

        Assert.False(result);
    }

    [Fact]
    public async Task AddMessageAsync_AssignsIncreasingSequenceNumbers()
    {
        var repo = CreateRepository(out _);
        var session = await repo.CreateSessionAsync("Chat");

        var first = await repo.AddMessageAsync(session.Id, MessageRoles.User, "one");
        var second = await repo.AddMessageAsync(session.Id, MessageRoles.Assistant, "two");
        var third = await repo.AddMessageAsync(session.Id, MessageRoles.User, "three");

        Assert.Equal(1, first!.Sequence);
        Assert.Equal(2, second!.Sequence);
        Assert.Equal(3, third!.Sequence);
    }

    [Fact]
    public async Task AddMessageAsync_UnknownSession_ReturnsNull()
    {
        var repo = CreateRepository(out _);

        var result = await repo.AddMessageAsync(Guid.NewGuid(), MessageRoles.User, "hi");

        Assert.Null(result);
    }

    [Fact]
    public async Task AddMessageAsync_BumpsSessionUpdatedAt()
    {
        var repo = CreateRepository(out _);
        var session = await repo.CreateSessionAsync("Chat");
        var originalUpdatedAt = session.UpdatedAt;

        await Task.Delay(10);
        await repo.AddMessageAsync(session.Id, MessageRoles.User, "hi");

        var reloaded = await repo.GetSessionWithMessagesAsync(session.Id);
        Assert.True(reloaded!.UpdatedAt > originalUpdatedAt);
    }

    [Fact]
    public async Task AddMessageAsync_FirstUserMessage_AutoGeneratesTitle_WhenTitleWasEmpty()
    {
        var repo = CreateRepository(out _);
        var session = await repo.CreateSessionAsync(null);

        await repo.AddMessageAsync(session.Id, MessageRoles.User, "What is the capital of France?");

        var reloaded = await repo.GetSessionWithMessagesAsync(session.Id);
        Assert.Equal("What is the capital of France?", reloaded!.Title);
    }

    [Fact]
    public async Task AddMessageAsync_DoesNotOverwriteExistingTitle()
    {
        var repo = CreateRepository(out _);
        var session = await repo.CreateSessionAsync("My custom title");

        await repo.AddMessageAsync(session.Id, MessageRoles.User, "Some message");

        var reloaded = await repo.GetSessionWithMessagesAsync(session.Id);
        Assert.Equal("My custom title", reloaded!.Title);
    }

    [Fact]
    public async Task AddMessageAsync_AssistantMessage_DoesNotSetTitle()
    {
        var repo = CreateRepository(out _);
        var session = await repo.CreateSessionAsync(null);

        await repo.AddMessageAsync(session.Id, MessageRoles.Assistant, "I am an assistant reply");

        var reloaded = await repo.GetSessionWithMessagesAsync(session.Id);
        Assert.Equal(string.Empty, reloaded!.Title);
    }

    [Fact]
    public async Task GetSessionWithMessagesAsync_ReturnsMessagesInSequenceOrder()
    {
        var repo = CreateRepository(out _);
        var session = await repo.CreateSessionAsync("Chat");
        await repo.AddMessageAsync(session.Id, MessageRoles.User, "first");
        await repo.AddMessageAsync(session.Id, MessageRoles.Assistant, "second");
        await repo.AddMessageAsync(session.Id, MessageRoles.User, "third");

        var reloaded = await repo.GetSessionWithMessagesAsync(session.Id);

        Assert.Equal(3, reloaded!.Messages.Count);
        Assert.Equal("first", reloaded.Messages[0].Content);
        Assert.Equal("second", reloaded.Messages[1].Content);
        Assert.Equal("third", reloaded.Messages[2].Content);
    }

    [Fact]
    public async Task Messages_FromDifferentSessions_NeverMix()
    {
        var repo = CreateRepository(out _);
        var sessionA = await repo.CreateSessionAsync("Session A");
        var sessionB = await repo.CreateSessionAsync("Session B");

        await repo.AddMessageAsync(sessionA.Id, MessageRoles.User, "A1");
        await repo.AddMessageAsync(sessionB.Id, MessageRoles.User, "B1");
        await repo.AddMessageAsync(sessionA.Id, MessageRoles.User, "A2");

        var messagesA = await repo.GetMessagesAsync(sessionA.Id);
        var messagesB = await repo.GetMessagesAsync(sessionB.Id);

        Assert.Equal(new[] { "A1", "A2" }, messagesA.Select(m => m.Content));
        Assert.Equal(new[] { "B1" }, messagesB.Select(m => m.Content));
    }

    [Fact]
    public async Task GetSessionWithMessagesAsync_UnknownSession_ReturnsNull()
    {
        var repo = CreateRepository(out _);

        var result = await repo.GetSessionWithMessagesAsync(Guid.NewGuid());

        Assert.Null(result);
    }
}
