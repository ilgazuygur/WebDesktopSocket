using System.Net.WebSockets;
using System.Text;

namespace Socket.Tests.Sockets;

// A minimal WebSocket test double for exercising WebSocketConnectionManager
// and ChatSocketHandler without a real network connection. Records every
// message sent through it, and - like the real System.Net.WebSockets
// implementation - throws if SendAsync is called again before a previous
// call on the same instance has finished, so tests can verify
// WebSocketConnectionManager's per-connection send lock actually prevents
// that from happening.
internal sealed class FakeWebSocket : WebSocket
{
    private readonly object _sendGate = new();
    private readonly object _sentMessagesGate = new();
    private readonly List<string> _sentMessages = new();
    private bool _sendInProgress;
    private WebSocketState _state = WebSocketState.Open;

    public IReadOnlyList<string> SentMessages
    {
        get { lock (_sentMessagesGate) { return _sentMessages.ToList(); } }
    }

    // Test convenience: lets a test discard e.g. the HelloAck sent during
    // setup, so later assertions only see messages sent after that point.
    public void ClearSentMessages()
    {
        lock (_sentMessagesGate)
        {
            _sentMessages.Clear();
        }
    }

    public WebSocketCloseStatus? ClosedWithStatus { get; private set; }

    // Lets a test simulate a slow send, creating a window during which a
    // real concurrency bug (two overlapping SendAsync calls) would show up.
    public TimeSpan SendDelay { get; set; } = TimeSpan.Zero;

    public void SetState(WebSocketState state) => _state = state;

    public override WebSocketCloseStatus? CloseStatus => ClosedWithStatus;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        lock (_sendGate)
        {
            if (_sendInProgress)
            {
                throw new InvalidOperationException("There is already one outstanding 'SendAsync' call for this WebSocket instance.");
            }
            _sendInProgress = true;
        }

        try
        {
            if (SendDelay > TimeSpan.Zero)
            {
                await Task.Delay(SendDelay, cancellationToken);
            }

            var text = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            lock (_sentMessagesGate)
            {
                _sentMessages.Add(text);
            }
        }
        finally
        {
            lock (_sendGate)
            {
                _sendInProgress = false;
            }
        }
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        // Not exercised by these tests - only Program.cs's own receive
        // loop calls ReceiveAsync, and that's covered separately by a
        // real WebApplicationFactory + ClientWebSocket integration test.
        throw new NotSupportedException();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        ClosedWithStatus = closeStatus;
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        ClosedWithStatus = closeStatus;
        return Task.CompletedTask;
    }

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
    }

    public override void Dispose()
    {
        _state = WebSocketState.Closed;
    }
}
