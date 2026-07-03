using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SocketDesktop.Core.Sockets;
using SocketShared.Protocol;

namespace SocketDesktop.Avalonia.Tests;

// A small controllable IClientWebSocket so a headless UI test can drive the
// view model through a real DesktopSocketClient (connect + register) and
// then assert the bound UI updated - without a real network connection.
internal sealed class HeadlessTestSocket : IClientWebSocket
{
    private readonly Channel<byte[]?> _incoming = Channel.CreateUnbounded<byte[]?>();
    public WebSocketState State { get; private set; } = WebSocketState.None;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        State = WebSocketState.Open;
        return Task.CompletedTask;
    }

    public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var item = await _incoming.Reader.ReadAsync(cancellationToken);
        if (item is null)
        {
            State = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
        Array.Copy(item, 0, buffer.Array!, buffer.Offset, item.Length);
        return new WebSocketReceiveResult(item.Length, WebSocketMessageType.Text, true);
    }

    public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        State = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public void Dispose() => State = WebSocketState.Closed;

    public void EnqueueMessage(SocketMessage message) =>
        _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)));
}

internal sealed class HeadlessTestSocketFactory : IClientWebSocketFactory
{
    private readonly HeadlessTestSocket _socket;
    public HeadlessTestSocketFactory(HeadlessTestSocket socket) => _socket = socket;
    public IClientWebSocket Create() => _socket;
}
