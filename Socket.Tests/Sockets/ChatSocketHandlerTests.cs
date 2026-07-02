using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SocketShared.Protocol;
using SocketWeb.Data;
using SocketWeb.Services;

namespace Socket.Tests.Sockets;

// Drives ChatSocketHandler exactly the way Program.cs's /ws endpoint
// does - by feeding it raw JSON strings and a server-assigned
// connectionId - against a WebSocketConnectionManager wired to
// FakeWebSocket connections and an EF Core InMemory-backed
// IChatRepository. This covers the actual routing/idempotency/
// persistence rules without needing a real network connection.
public class ChatSocketHandlerTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly WebSocketConnectionManager _manager;
    private readonly ChatSocketHandler _handler;
    private readonly IChatRepository _repo;

    public ChatSocketHandlerTests()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<ChatDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddScoped<IChatRepository, ChatRepository>();
        _provider = services.BuildServiceProvider();

        _manager = new WebSocketConnectionManager();
        _handler = new ChatSocketHandler(_manager, _provider.GetRequiredService<IServiceScopeFactory>());

        // A dedicated repository the tests use to set up sessions and
        // assert on saved messages - backed by the same in-memory
        // database name, so it sees exactly what the handler's own
        // (separately-scoped) repository calls write.
        _repo = _provider.CreateScope().ServiceProvider.GetRequiredService<IChatRepository>();
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task ClientHello_Browser_ReceivesHelloAck()
    {
        var socket = new FakeWebSocket();
        var connectionId = _manager.AddConnection(socket);

        await SendAsync(connectionId, new SocketMessage { Type = MessageType.ClientHello, Role = ClientRole.Browser });

        var ack = Deserialize(Assert.Single(socket.SentMessages));
        Assert.Equal(MessageType.HelloAck, ack.Type);
        Assert.Equal(ClientRole.Browser, ack.Role);
        Assert.Equal(connectionId, ack.ConnectionId);
    }

    [Fact]
    public async Task ClientHello_Desktop_MakesConnectionTheActiveDesktop()
    {
        var (_, _) = await RegisterAsync(ClientRole.Desktop);

        Assert.True(_manager.IsDesktopOnline);
    }

    [Fact]
    public async Task ClientHello_WithoutRole_SendsError()
    {
        var socket = new FakeWebSocket();
        var connectionId = _manager.AddConnection(socket);

        await SendAsync(connectionId, new SocketMessage { Type = MessageType.ClientHello });

        var response = Deserialize(Assert.Single(socket.SentMessages));
        Assert.Equal(MessageType.Error, response.Type);
    }

    [Fact]
    public async Task MessageBeforeClientHello_IsRejectedWithError()
    {
        var socket = new FakeWebSocket();
        var connectionId = _manager.AddConnection(socket);

        await SendAsync(connectionId, new SocketMessage { Type = MessageType.UserPrompt, SessionId = Guid.NewGuid().ToString(), RequestId = "r1", Content = "hi" });

        var response = Deserialize(Assert.Single(socket.SentMessages));
        Assert.Equal(MessageType.Error, response.Type);
    }

    [Fact]
    public async Task InvalidJson_SendsErrorWithoutCrashing()
    {
        var socket = new FakeWebSocket();
        var connectionId = _manager.AddConnection(socket);

        await _handler.HandleMessageAsync(connectionId, "{ this is not valid json", CancellationToken.None);

        var response = Deserialize(Assert.Single(socket.SentMessages));
        Assert.Equal(MessageType.Error, response.Type);
    }

    [Fact]
    public async Task UserPrompt_RoutesAiRequest_OnlyToDesktop_NotToOtherBrowsers()
    {
        var session = await _repo.CreateSessionAsync("Test session");
        var (browserId, browserSocket) = await RegisterAsync(ClientRole.Browser);
        var (_, desktopSocket) = await RegisterAsync(ClientRole.Desktop);
        var (_, otherBrowserSocket) = await RegisterAsync(ClientRole.Browser);
        browserSocket.ClearSentMessages();
        desktopSocket.ClearSentMessages();
        otherBrowserSocket.ClearSentMessages();

        await SendAsync(browserId, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = session.Id.ToString(),
            RequestId = "req-1",
            Content = "Hello AI"
        });

        var desktopMessage = Deserialize(Assert.Single(desktopSocket.SentMessages));
        Assert.Equal(MessageType.AiRequest, desktopMessage.Type);
        Assert.Equal(session.Id.ToString(), desktopMessage.SessionId);
        Assert.Equal("req-1", desktopMessage.RequestId);
        Assert.Empty(otherBrowserSocket.SentMessages);

        var thinking = Deserialize(Assert.Single(browserSocket.SentMessages));
        Assert.Equal(MessageType.Status, thinking.Type);
        Assert.Equal(SocketStatusCodes.Thinking, thinking.Content);
    }

    [Fact]
    public async Task UserPrompt_NoDesktopOnline_SendsClearErrorAndDoesNotLeavePending()
    {
        var session = await _repo.CreateSessionAsync("Test session");
        var (browserId, browserSocket) = await RegisterAsync(ClientRole.Browser);
        browserSocket.ClearSentMessages();

        await SendAsync(browserId, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = session.Id.ToString(),
            RequestId = "req-1",
            Content = "Hello AI"
        });

        var response = Deserialize(Assert.Single(browserSocket.SentMessages));
        Assert.Equal(MessageType.Error, response.Type);

        // "Not left permanently pending" - a retry with the same
        // RequestId (e.g. after the desktop reconnects) must be able to
        // go through, which only works if the failed attempt cleaned up.
        var (_, desktopSocket) = await RegisterAsync(ClientRole.Desktop);
        await SendAsync(browserId, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = session.Id.ToString(),
            RequestId = "req-1",
            Content = "Hello AI"
        });

        Assert.Contains(desktopSocket.SentMessages, m => Deserialize(m).Type == MessageType.AiRequest);
    }

    [Fact]
    public async Task UserPrompt_UnknownSession_SendsError_AndSavesNothing()
    {
        var (browserId, browserSocket) = await RegisterAsync(ClientRole.Browser);
        browserSocket.ClearSentMessages();

        await SendAsync(browserId, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = Guid.NewGuid().ToString(),
            RequestId = "req-1",
            Content = "Hello AI"
        });

        var response = Deserialize(Assert.Single(browserSocket.SentMessages));
        Assert.Equal(MessageType.Error, response.Type);
    }

    [Fact]
    public async Task UserPrompt_EmptyContent_SendsError()
    {
        var session = await _repo.CreateSessionAsync("Test session");
        var (browserId, browserSocket) = await RegisterAsync(ClientRole.Browser);
        browserSocket.ClearSentMessages();

        await SendAsync(browserId, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = session.Id.ToString(),
            RequestId = "req-1",
            Content = "   "
        });

        var response = Deserialize(Assert.Single(browserSocket.SentMessages));
        Assert.Equal(MessageType.Error, response.Type);

        var messages = await _repo.GetMessagesAsync(session.Id);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task UserPrompt_OversizedContent_SendsError()
    {
        var session = await _repo.CreateSessionAsync("Test session");
        var (browserId, browserSocket) = await RegisterAsync(ClientRole.Browser);
        browserSocket.ClearSentMessages();

        await SendAsync(browserId, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = session.Id.ToString(),
            RequestId = "req-1",
            Content = new string('a', 8001)
        });

        var response = Deserialize(Assert.Single(browserSocket.SentMessages));
        Assert.Equal(MessageType.Error, response.Type);
    }

    [Fact]
    public async Task UserPrompt_SameRequestIdTwice_SavesUserMessageExactlyOnce()
    {
        var session = await _repo.CreateSessionAsync("Test session");
        var (browserId, _) = await RegisterAsync(ClientRole.Browser);
        var (_, desktopSocket) = await RegisterAsync(ClientRole.Desktop);

        var prompt = new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = session.Id.ToString(),
            RequestId = "req-dup",
            Content = "Hello AI"
        };

        await SendAsync(browserId, prompt);
        await SendAsync(browserId, prompt); // duplicate - same RequestId

        var messages = await _repo.GetMessagesAsync(session.Id);
        Assert.Single(messages);
        Assert.Equal(MessageRoles.User, messages[0].Role);

        // Only the first attempt should have reached the desktop.
        var aiRequests = desktopSocket.SentMessages.Select(Deserialize).Count(m => m.Type == MessageType.AiRequest);
        Assert.Equal(1, aiRequests);
    }

    [Fact]
    public async Task UserPrompt_History_NeverMixesSessions()
    {
        var sessionA = await _repo.CreateSessionAsync("Session A");
        var sessionB = await _repo.CreateSessionAsync("Session B");
        await _repo.AddMessageAsync(sessionB.Id, MessageRoles.User, "This belongs to session B only");

        var (browserId, _) = await RegisterAsync(ClientRole.Browser);
        var (_, desktopSocket) = await RegisterAsync(ClientRole.Desktop);
        desktopSocket.ClearSentMessages();

        await SendAsync(browserId, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = sessionA.Id.ToString(),
            RequestId = "req-a",
            Content = "Hello from session A"
        });

        var aiRequest = Deserialize(Assert.Single(desktopSocket.SentMessages));
        Assert.All(aiRequest.History!, turn => Assert.DoesNotContain("session B", turn.Content));
        Assert.Single(aiRequest.History!);
    }

    [Fact]
    public async Task AiResponse_SavesAssistantMessage_AndRoutesToOriginatingBrowser()
    {
        var session = await _repo.CreateSessionAsync("Test session");
        var (browserId, browserSocket) = await RegisterAsync(ClientRole.Browser);
        var (desktopId, _) = await RegisterAsync(ClientRole.Desktop);
        var beforeUpdatedAt = session.UpdatedAt;

        await SendAsync(browserId, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = session.Id.ToString(),
            RequestId = "req-1",
            Content = "Hello AI"
        });
        browserSocket.ClearSentMessages();

        await SendAsync(desktopId, new SocketMessage
        {
            Type = MessageType.AiResponse,
            SessionId = session.Id.ToString(),
            RequestId = "req-1",
            Content = "Hello human!"
        });

        var routed = Deserialize(Assert.Single(browserSocket.SentMessages));
        Assert.Equal(MessageType.AiResponse, routed.Type);
        Assert.Equal("Hello human!", routed.Content);

        var messages = await _repo.GetMessagesAsync(session.Id);
        Assert.Contains(messages, m => m.Role == MessageRoles.Assistant && m.Content == "Hello human!");

        var reloaded = await _repo.GetSessionWithMessagesAsync(session.Id);
        Assert.True(reloaded!.UpdatedAt >= beforeUpdatedAt);
    }

    [Fact]
    public async Task AiResponse_SameRequestIdTwice_SavesAssistantMessageExactlyOnce()
    {
        var session = await _repo.CreateSessionAsync("Test session");
        var (browserId, _) = await RegisterAsync(ClientRole.Browser);
        var (desktopId, _) = await RegisterAsync(ClientRole.Desktop);

        await SendAsync(browserId, new SocketMessage { Type = MessageType.UserPrompt, SessionId = session.Id.ToString(), RequestId = "req-1", Content = "Hi" });

        var response = new SocketMessage { Type = MessageType.AiResponse, SessionId = session.Id.ToString(), RequestId = "req-1", Content = "Hello!" };
        await SendAsync(desktopId, response);
        await SendAsync(desktopId, response); // duplicate

        var messages = await _repo.GetMessagesAsync(session.Id);
        Assert.Single(messages, m => m.Role == MessageRoles.Assistant);
    }

    [Fact]
    public async Task AiResponse_UnknownRequestId_IsIgnoredSafely()
    {
        var (desktopId, desktopSocket) = await RegisterAsync(ClientRole.Desktop);
        desktopSocket.ClearSentMessages();

        await SendAsync(desktopId, new SocketMessage
        {
            Type = MessageType.AiResponse,
            SessionId = Guid.NewGuid().ToString(),
            RequestId = "never-requested",
            Content = "Surprise!"
        });

        // No crash, and nothing sent back on this connection either -
        // there's no way to know who to route it to.
        Assert.Empty(desktopSocket.SentMessages);
    }

    [Fact]
    public async Task AiResponse_FromBrowserConnection_IsIgnored()
    {
        var session = await _repo.CreateSessionAsync("Test session");
        var (browserId, browserSocket) = await RegisterAsync(ClientRole.Browser);
        var (_, _) = await RegisterAsync(ClientRole.Desktop);

        await SendAsync(browserId, new SocketMessage { Type = MessageType.UserPrompt, SessionId = session.Id.ToString(), RequestId = "req-1", Content = "Hi" });
        browserSocket.ClearSentMessages();

        // A browser is not allowed to impersonate the desktop and inject
        // an AiResponse directly.
        await SendAsync(browserId, new SocketMessage { Type = MessageType.AiResponse, SessionId = session.Id.ToString(), RequestId = "req-1", Content = "Spoofed!" });

        var messages = await _repo.GetMessagesAsync(session.Id);
        Assert.DoesNotContain(messages, m => m.Content == "Spoofed!");
    }

    [Fact]
    public async Task DesktopError_RoutesToOriginatingBrowser_AndDoesNotSaveAssistantMessage()
    {
        var session = await _repo.CreateSessionAsync("Test session");
        var (browserId, browserSocket) = await RegisterAsync(ClientRole.Browser);
        var (desktopId, _) = await RegisterAsync(ClientRole.Desktop);

        await SendAsync(browserId, new SocketMessage { Type = MessageType.UserPrompt, SessionId = session.Id.ToString(), RequestId = "req-1", Content = "Hi" });
        browserSocket.ClearSentMessages();

        await SendAsync(desktopId, new SocketMessage { Type = MessageType.Error, RequestId = "req-1", Error = "AI API authentication failed." });

        var routed = Deserialize(Assert.Single(browserSocket.SentMessages));
        Assert.Equal(MessageType.Error, routed.Type);
        Assert.Equal("req-1", routed.RequestId);

        var messages = await _repo.GetMessagesAsync(session.Id);
        Assert.DoesNotContain(messages, m => m.Role == MessageRoles.Assistant);
    }

    [Fact]
    public async Task DesktopError_ThenRetryWithSameRequestId_Succeeds()
    {
        var session = await _repo.CreateSessionAsync("Test session");
        var (browserId, _) = await RegisterAsync(ClientRole.Browser);
        var (desktopId, desktopSocket) = await RegisterAsync(ClientRole.Desktop);

        await SendAsync(browserId, new SocketMessage { Type = MessageType.UserPrompt, SessionId = session.Id.ToString(), RequestId = "req-1", Content = "Hi" });
        await SendAsync(desktopId, new SocketMessage { Type = MessageType.Error, RequestId = "req-1", Error = "Timeout." });
        desktopSocket.ClearSentMessages();

        // A fresh prompt reusing the same RequestId should be treated as
        // brand new, since the failed one was cleaned up.
        await SendAsync(browserId, new SocketMessage { Type = MessageType.UserPrompt, SessionId = session.Id.ToString(), RequestId = "req-1", Content = "Hi again" });

        Assert.Contains(desktopSocket.SentMessages, m => Deserialize(m).Type == MessageType.AiRequest);
    }

    [Fact]
    public async Task BrowserDisconnect_WhileRequestPending_AssistantResponseIsStillPersisted()
    {
        var session = await _repo.CreateSessionAsync("Test session");
        var (browserId, _) = await RegisterAsync(ClientRole.Browser);
        var (desktopId, _) = await RegisterAsync(ClientRole.Desktop);

        await SendAsync(browserId, new SocketMessage { Type = MessageType.UserPrompt, SessionId = session.Id.ToString(), RequestId = "req-1", Content = "Hi" });

        // The browser disconnects before the AI responds.
        await _manager.RemoveConnectionAsync(browserId);

        // The response arrives after the browser is already gone -
        // routing will silently fail, but persistence must still happen.
        await SendAsync(desktopId, new SocketMessage { Type = MessageType.AiResponse, SessionId = session.Id.ToString(), RequestId = "req-1", Content = "Here's your answer" });

        var messages = await _repo.GetMessagesAsync(session.Id);
        Assert.Contains(messages, m => m.Role == MessageRoles.Assistant && m.Content == "Here's your answer");
    }

    private async Task<(string connectionId, FakeWebSocket socket)> RegisterAsync(ClientRole role)
    {
        var socket = new FakeWebSocket();
        var connectionId = _manager.AddConnection(socket);
        await SendAsync(connectionId, new SocketMessage { Type = MessageType.ClientHello, Role = role });
        return (connectionId, socket);
    }

    private Task SendAsync(string connectionId, SocketMessage message)
    {
        return _handler.HandleMessageAsync(connectionId, JsonSerializer.Serialize(message), CancellationToken.None);
    }

    private static SocketMessage Deserialize(string json) => JsonSerializer.Deserialize<SocketMessage>(json)!;
}
