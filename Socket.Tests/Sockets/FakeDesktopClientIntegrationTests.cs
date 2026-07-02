using System.Net.Http.Json;
using System.Net.WebSockets;
using Socket.Tests.TestInfrastructure;
using SocketShared.Protocol;
using SocketWeb.Api;

namespace Socket.Tests.Sockets;

// Exercises the full Browser -> SocketWeb -> "fake Desktop" -> SocketWeb
// -> Browser flow over two real WebSocket connections through TestServer.
// The "fake Desktop" here is just a hand-rolled client that speaks the
// same SocketMessage protocol SocketDesktop's DesktopSocketClient does -
// it is a test double, not a second production desktop implementation,
// and exists specifically so this flow can be verified on macOS without
// a real Windows/WPF process or a live AI API.
public class FakeDesktopClientIntegrationTests : IClassFixture<InMemoryWebApplicationFactory>
{
    private readonly InMemoryWebApplicationFactory _factory;

    public FakeDesktopClientIntegrationTests(InMemoryWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task FullRoundTrip_UserPromptToAiResponse_IsRoutedAndPersistedCorrectly()
    {
        var httpClient = _factory.CreateClient();
        var session = await CreateSessionAsync(httpClient, "Integration test session");

        using var browserSocket = await ConnectAndRegisterAsync(ClientRole.Browser);
        using var desktopSocket = await ConnectAndRegisterAsync(ClientRole.Desktop);

        // The browser was already connected when the desktop registered,
        // so it should be told the desktop is now online.
        var onlineStatus = await WebSocketTestHelpers.ReceiveMessageAsync(browserSocket);
        Assert.Equal(MessageType.Status, onlineStatus.Type);
        Assert.Equal(SocketStatusCodes.DesktopOnline, onlineStatus.Content);

        var requestId = Guid.NewGuid().ToString();
        await WebSocketTestHelpers.SendMessageAsync(browserSocket, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = session.Id.ToString(),
            RequestId = requestId,
            Content = "What is the capital of France?"
        });

        var thinking = await WebSocketTestHelpers.ReceiveMessageAsync(browserSocket);
        Assert.Equal(MessageType.Status, thinking.Type);
        Assert.Equal(SocketStatusCodes.Thinking, thinking.Content);

        var aiRequest = await WebSocketTestHelpers.ReceiveMessageAsync(desktopSocket);
        Assert.Equal(MessageType.AiRequest, aiRequest.Type);
        Assert.Equal(session.Id.ToString(), aiRequest.SessionId);
        Assert.Equal(requestId, aiRequest.RequestId);
        Assert.NotNull(aiRequest.History);
        Assert.Single(aiRequest.History!);
        Assert.Equal("What is the capital of France?", aiRequest.History![0].Content);

        // Reply exactly as SocketDesktop's real DesktopSocketClient would
        // on a successful AI call.
        await WebSocketTestHelpers.SendMessageAsync(desktopSocket, new SocketMessage
        {
            Type = MessageType.AiResponse,
            SessionId = aiRequest.SessionId,
            RequestId = aiRequest.RequestId,
            Content = "Paris.",
            MessageRole = "assistant"
        });

        var response = await WebSocketTestHelpers.ReceiveMessageAsync(browserSocket);
        Assert.Equal(MessageType.AiResponse, response.Type);
        Assert.Equal(requestId, response.RequestId);
        Assert.Equal("Paris.", response.Content);

        // And it's genuinely persisted - reload through the REST API the
        // same way a fresh page load or reconnect would.
        var detail = await httpClient.GetFromJsonAsync<ChatSessionDetailDto>($"/api/sessions/{session.Id}");
        Assert.Equal(2, detail!.Messages.Count);
        Assert.Equal("user", detail.Messages[0].Role);
        Assert.Equal("What is the capital of France?", detail.Messages[0].Content);
        Assert.Equal("assistant", detail.Messages[1].Role);
        Assert.Equal("Paris.", detail.Messages[1].Content);
    }

    [Fact]
    public async Task TwoBrowsersInDifferentSessions_NeverReceiveEachOthersResponses()
    {
        var httpClient = _factory.CreateClient();
        var sessionA = await CreateSessionAsync(httpClient, "Session A");
        var sessionB = await CreateSessionAsync(httpClient, "Session B");

        using var browserA = await ConnectAndRegisterAsync(ClientRole.Browser);
        using var browserB = await ConnectAndRegisterAsync(ClientRole.Browser);
        using var desktopSocket = await ConnectAndRegisterAsync(ClientRole.Desktop);

        await WebSocketTestHelpers.ReceiveMessageAsync(browserA); // desktop-online
        await WebSocketTestHelpers.ReceiveMessageAsync(browserB); // desktop-online

        var requestIdA = Guid.NewGuid().ToString();
        await WebSocketTestHelpers.SendMessageAsync(browserA, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = sessionA.Id.ToString(),
            RequestId = requestIdA,
            Content = "Message from A"
        });
        await WebSocketTestHelpers.ReceiveMessageAsync(browserA); // thinking
        var aiRequestA = await WebSocketTestHelpers.ReceiveMessageAsync(desktopSocket);

        await WebSocketTestHelpers.SendMessageAsync(desktopSocket, new SocketMessage
        {
            Type = MessageType.AiResponse,
            SessionId = aiRequestA.SessionId,
            RequestId = aiRequestA.RequestId,
            Content = "Reply for A"
        });

        var responseForA = await WebSocketTestHelpers.ReceiveMessageAsync(browserA);
        Assert.Equal("Reply for A", responseForA.Content);

        var requestIdB = Guid.NewGuid().ToString();
        await WebSocketTestHelpers.SendMessageAsync(browserB, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = sessionB.Id.ToString(),
            RequestId = requestIdB,
            Content = "Message from B"
        });
        await WebSocketTestHelpers.ReceiveMessageAsync(browserB); // thinking
        var aiRequestB = await WebSocketTestHelpers.ReceiveMessageAsync(desktopSocket);
        Assert.Equal(sessionB.Id.ToString(), aiRequestB.SessionId);
        Assert.Single(aiRequestB.History!); // only session B's own message - never mixed with A's

        await WebSocketTestHelpers.SendMessageAsync(desktopSocket, new SocketMessage
        {
            Type = MessageType.AiResponse,
            SessionId = aiRequestB.SessionId,
            RequestId = aiRequestB.RequestId,
            Content = "Reply for B"
        });

        var responseForB = await WebSocketTestHelpers.ReceiveMessageAsync(browserB);
        Assert.Equal("Reply for B", responseForB.Content);
        Assert.Equal(requestIdB, responseForB.RequestId);
    }

    [Fact]
    public async Task DesktopOffline_UserPromptGetsClearError_ThenSucceedsOnceDesktopConnects()
    {
        var httpClient = _factory.CreateClient();
        var session = await CreateSessionAsync(httpClient, "No desktop session");

        using var browserSocket = await ConnectAndRegisterAsync(ClientRole.Browser);

        var requestId = Guid.NewGuid().ToString();
        await WebSocketTestHelpers.SendMessageAsync(browserSocket, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = session.Id.ToString(),
            RequestId = requestId,
            Content = "Anyone there?"
        });

        var error = await WebSocketTestHelpers.ReceiveMessageAsync(browserSocket);
        Assert.Equal(MessageType.Error, error.Type);
        Assert.Equal(requestId, error.RequestId);

        // A desktop connecting afterwards, and the SAME RequestId being
        // retried, should succeed - proving the earlier failure didn't
        // leave the request permanently stuck.
        using var desktopSocket = await ConnectAndRegisterAsync(ClientRole.Desktop);
        await WebSocketTestHelpers.ReceiveMessageAsync(browserSocket); // desktop-online

        await WebSocketTestHelpers.SendMessageAsync(browserSocket, new SocketMessage
        {
            Type = MessageType.UserPrompt,
            SessionId = session.Id.ToString(),
            RequestId = requestId,
            Content = "Anyone there?"
        });
        await WebSocketTestHelpers.ReceiveMessageAsync(browserSocket); // thinking
        var aiRequest = await WebSocketTestHelpers.ReceiveMessageAsync(desktopSocket);
        Assert.Equal(MessageType.AiRequest, aiRequest.Type);
        Assert.Equal(requestId, aiRequest.RequestId);
    }

    private static async Task<ChatSessionSummaryDto> CreateSessionAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/api/sessions", new CreateSessionRequest(title));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatSessionSummaryDto>())!;
    }

    private async Task<WebSocket> ConnectAndRegisterAsync(ClientRole role)
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var wsUri = new UriBuilder(_factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws" }.Uri;
        var socket = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

        await WebSocketTestHelpers.SendMessageAsync(socket, new SocketMessage { Type = MessageType.ClientHello, Role = role });
        var ack = await WebSocketTestHelpers.ReceiveMessageAsync(socket);
        Assert.Equal(MessageType.HelloAck, ack.Type);

        return socket;
    }
}
