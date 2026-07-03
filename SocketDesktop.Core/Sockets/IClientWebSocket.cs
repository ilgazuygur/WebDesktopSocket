using System.Net.WebSockets;

namespace SocketDesktop.Core.Sockets;

// A thin abstraction over System.Net.WebSockets.ClientWebSocket. Its only
// reason to exist is testability: DesktopSocketClient's connect/receive/
// reconnect loop is the interesting logic, and injecting a fake socket
// (instead of opening a real network connection) is what lets that logic
// be unit-tested deterministically - make ConnectAsync throw, feed
// canned messages, simulate the server closing, etc.
public interface IClientWebSocket : IDisposable
{
    WebSocketState State { get; }

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken);

    Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken);

    Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken);
}

// Creates a fresh IClientWebSocket per connection attempt (a WebSocket
// can't be reused after it closes, so reconnecting needs a new one).
public interface IClientWebSocketFactory
{
    IClientWebSocket Create();
}
