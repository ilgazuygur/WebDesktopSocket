using System.Net.WebSockets;
using Socket.Tests.TestInfrastructure;
using SocketShared.Protocol;

namespace Socket.Tests.Sockets;

// Connects to the real /ws endpoint (through TestServer's in-memory
// WebSocket client, not a real network socket) to exercise the actual
// receive loop in Program.cs - specifically the framing/size-limit logic
// that ChatSocketHandlerTests can't reach, since that logic lives in the
// endpoint delegate itself, not in ChatSocketHandler.
public class WebSocketWireIntegrationTests : IClassFixture<InMemoryWebApplicationFactory>
{
    private readonly InMemoryWebApplicationFactory _factory;

    public WebSocketWireIntegrationTests(InMemoryWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task InvalidJson_DoesNotCrashTheConnection_ServerKeepsResponding()
    {
        using var socket = await ConnectAsync();

        await WebSocketTestHelpers.SendTextAsync(socket, "{ this is not valid json at all");

        var error = await WebSocketTestHelpers.ReceiveMessageAsync(socket);
        Assert.Equal(MessageType.Error, error.Type);

        // The connection must still be usable afterwards - prove it by
        // completing a normal ClientHello/HelloAck round trip.
        await WebSocketTestHelpers.SendMessageAsync(socket, new SocketMessage { Type = MessageType.ClientHello, Role = ClientRole.Browser });

        var ack = await WebSocketTestHelpers.ReceiveMessageAsync(socket);
        Assert.Equal(MessageType.HelloAck, ack.Type);
    }

    [Fact]
    public async Task OversizedMessage_ClosesConnectionWithMessageTooBig()
    {
        using var socket = await ConnectAsync();

        // Comfortably larger than the server's 256 KB per-message cap.
        var oversized = new string('a', 300 * 1024);
        await WebSocketTestHelpers.SendTextAsync(socket, oversized);

        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, result.CloseStatus);
    }

    private async Task<WebSocket> ConnectAsync()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var httpUri = _factory.Server.BaseAddress;
        var wsUri = new UriBuilder(httpUri) { Scheme = "ws", Path = "/ws" }.Uri;
        return await wsClient.ConnectAsync(wsUri, CancellationToken.None);
    }
}
