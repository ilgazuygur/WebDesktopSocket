using System.Net.WebSockets;

namespace SocketDesktop.Core.Sockets;

// The production implementation: a straight pass-through to the real
// System.Net.WebSockets.ClientWebSocket. All the actual connection logic
// lives in DesktopSocketClient; this just adapts the concrete type to the
// IClientWebSocket interface.
public sealed class RealClientWebSocket : IClientWebSocket
{
    private readonly ClientWebSocket _socket = new();

    public WebSocketState State => _socket.State;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        _socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        _socket.ReceiveAsync(buffer, cancellationToken);

    public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        _socket.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public void Dispose() => _socket.Dispose();
}

public sealed class RealClientWebSocketFactory : IClientWebSocketFactory
{
    public IClientWebSocket Create() => new RealClientWebSocket();
}
