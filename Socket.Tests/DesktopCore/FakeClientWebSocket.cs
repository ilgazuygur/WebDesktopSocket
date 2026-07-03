using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SocketDesktop.Core.Sockets;
using SocketShared.Protocol;

namespace Socket.Tests.DesktopCore;

// A controllable IClientWebSocket for testing DesktopSocketClient's
// connect/receive/reconnect loop without any real network. Tests enqueue
// the messages the client should "receive", can make ConnectAsync throw,
// and can inspect what the client sent back.
internal sealed class FakeClientWebSocket : IClientWebSocket
{
    private readonly Channel<ReceiveItem> _incoming = Channel.CreateUnbounded<ReceiveItem>();
    private readonly object _sentGate = new();
    private readonly List<string> _sent = new();

    public bool ConnectShouldThrow { get; set; }
    public WebSocketState State { get; private set; } = WebSocketState.None;
    public bool ConnectWasCalled { get; private set; }

    public IReadOnlyList<string> SentMessages
    {
        get { lock (_sentGate) { return _sent.ToList(); } }
    }

    public IReadOnlyList<SocketMessage> SentSocketMessages =>
        SentMessages.Select(json => JsonSerializer.Deserialize<SocketMessage>(json)!).ToList();

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ConnectWasCalled = true;
        if (ConnectShouldThrow)
        {
            throw new WebSocketException("Simulated connect failure");
        }
        State = WebSocketState.Open;
        return Task.CompletedTask;
    }

    public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        var text = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
        lock (_sentGate)
        {
            _sent.Add(text);
        }
        return Task.CompletedTask;
    }

    public async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var item = await _incoming.Reader.ReadAsync(cancellationToken);
        if (item.IsClose)
        {
            State = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        var bytes = item.Bytes!;
        Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
        return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
    }

    public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        State = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public void Dispose() => State = WebSocketState.Closed;

    // --- test helpers ---

    public void EnqueueMessage(SocketMessage message) =>
        _incoming.Writer.TryWrite(ReceiveItem.Text(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message))));

    public void EnqueueServerClose() => _incoming.Writer.TryWrite(ReceiveItem.Close());

    private readonly struct ReceiveItem
    {
        public bool IsClose { get; private init; }
        public byte[]? Bytes { get; private init; }

        public static ReceiveItem Text(byte[] bytes) => new() { IsClose = false, Bytes = bytes };
        public static ReceiveItem Close() => new() { IsClose = true, Bytes = null };
    }
}

// Hands out pre-built fake sockets in order (one per connection attempt),
// so reconnect tests can control what each successive attempt sees.
internal sealed class FakeClientWebSocketFactory : IClientWebSocketFactory
{
    private readonly Queue<FakeClientWebSocket> _queue;
    private readonly object _gate = new();
    public List<FakeClientWebSocket> Created { get; } = new();

    public FakeClientWebSocketFactory(params FakeClientWebSocket[] sockets)
    {
        _queue = new Queue<FakeClientWebSocket>(sockets);
    }

    public IClientWebSocket Create()
    {
        lock (_gate)
        {
            // After the scripted sockets run out, hand back a fresh socket
            // that simply stays open (a stable "connected" state) so a test
            // that only cares about the first few attempts doesn't spin.
            var socket = _queue.Count > 0 ? _queue.Dequeue() : new FakeClientWebSocket();
            Created.Add(socket);
            return socket;
        }
    }
}
