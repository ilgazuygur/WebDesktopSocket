using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SocketDesktop.Core.Sockets;
using SocketShared.Ai;
using SocketShared.Protocol;

namespace SocketDesktop.Core;

// The UI-independent heart of the desktop AI bridge. Connects to
// SocketWeb's /ws endpoint, registers as the Desktop client, and handles
// every AiRequest by calling IAiClient (the ONLY place in the whole
// solution that calls it) - replying with AiResponse on success or a
// specific, safe Error on failure. It also owns the connection lifecycle:
// a single managed loop that connects, runs until the connection drops,
// then automatically reconnects with bounded exponential backoff.
//
// This class has no Avalonia (or other UI) dependency, so the desktop UI
// (SocketDesktop.Avalonia) uses it unchanged, and it can be unit-tested
// against a fake IClientWebSocket.
public sealed class DesktopSocketClient : IAsyncDisposable
{
    private readonly IAiClient _aiClient;
    private readonly DesktopClientOptions _options;
    private readonly IClientWebSocketFactory _socketFactory;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _lifecycleLock = new();

    // Guards against handling the same AiRequest twice (e.g. redelivered
    // around a reconnect). Never cleared - fine for a desktop session's
    // lifetime; a long-running service would trim old entries.
    private readonly ConcurrentDictionary<string, byte> _handledRequestIds = new();

    private CancellationTokenSource? _outerCts;   // cancels the whole loop (Stop/Dispose)
    private CancellationTokenSource? _connectionCts; // cancels just the current connection (reconnect-now)
    private Task? _loopTask;
    private volatile bool _reconnectRequested;
    private IClientWebSocket? _currentSocket;

    public DesktopSocketClient(IAiClient aiClient, DesktopClientOptions options, IClientWebSocketFactory socketFactory)
    {
        _aiClient = aiClient;
        _options = options;
        _socketFactory = socketFactory;
    }

    public DesktopConnectionState State { get; private set; } = DesktopConnectionState.Disconnected;

    // Fired whenever State changes. Consumers on a UI thread should marshal
    // back to it (the loop runs on a background thread).
    public event Action<DesktopConnectionState>? StateChanged;

    // A short, human-readable, secret-free line for the activity log.
    public event Action<string>? ActivityLogged;

    // Null == idle; otherwise a short description of the AiRequest in flight.
    public event Action<string?>? CurrentRequestChanged;

    // Begins (or resumes) the managed connect + auto-reconnect loop.
    // Idempotent: calling it again while already running does nothing, so
    // a double-click on "Connect" can't start two competing loops.
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_loopTask is { IsCompleted: false })
            {
                return;
            }

            _outerCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_outerCts.Token));
        }
    }

    // Gracefully stops the loop and closes the current connection.
    public async Task StopAsync()
    {
        Task? loopTask;
        lock (_lifecycleLock)
        {
            if (_loopTask is null)
            {
                return;
            }
            _outerCts?.Cancel();
            _connectionCts?.Cancel();
            loopTask = _loopTask;
            _loopTask = null;
        }

        try
        {
            await loopTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation.
        }

        SetState(DesktopConnectionState.Disconnected);
    }

    // Interrupts a reconnect backoff wait (retry immediately) or drops a
    // live connection so it reconnects at once. Useful behind a "Reconnect"
    // button when the user doesn't want to wait out the backoff.
    public void RequestReconnectNow()
    {
        _reconnectRequested = true;
        lock (_lifecycleLock)
        {
            _connectionCts?.Cancel();
        }
    }

    private async Task RunLoopAsync(CancellationToken outerToken)
    {
        var backoff = _options.InitialReconnectDelay;
        var firstAttempt = true;

        while (!outerToken.IsCancellationRequested)
        {
            SetState(firstAttempt ? DesktopConnectionState.Connecting : DesktopConnectionState.Reconnecting);
            firstAttempt = false;

            var registered = await ConnectAndRunAsync(outerToken);

            if (outerToken.IsCancellationRequested)
            {
                break;
            }

            // A connection that actually registered resets the backoff, so
            // a brief blip doesn't inherit a long delay from earlier failures.
            if (registered)
            {
                backoff = _options.InitialReconnectDelay;
            }

            SetState(DesktopConnectionState.Reconnecting);

            if (_reconnectRequested)
            {
                _reconnectRequested = false; // skip the wait, retry immediately
            }
            else
            {
                try
                {
                    await Task.Delay(backoff, outerToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                backoff = NextBackoff(backoff);
            }
        }

        SetState(DesktopConnectionState.Disconnected);
    }

    // Opens one connection, registers, and pumps the receive loop until the
    // connection ends (closed, dropped, or cancelled). Returns whether it
    // successfully registered (received HelloAck) during this attempt.
    private async Task<bool> ConnectAndRunAsync(CancellationToken outerToken)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        lock (_lifecycleLock)
        {
            _connectionCts = connectionCts;
        }
        var token = connectionCts.Token;

        var socket = _socketFactory.Create();
        _currentSocket = socket;
        var registered = false;

        try
        {
            await socket.ConnectAsync(new Uri(_options.SocketUrl), token);
            Log("Connected to SocketWeb.");
            await SendAsync(socket, new SocketMessage { Type = MessageType.ClientHello, Role = ClientRole.Desktop }, token);

            var buffer = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(messageStream.ToArray());
                registered |= await HandleMessageAsync(socket, json, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Reconnect-now or Stop cancelled this connection - expected.
        }
        catch (Exception ex)
        {
            Log($"Connection to SocketWeb lost: {ex.Message}");
        }
        finally
        {
            lock (_lifecycleLock)
            {
                _connectionCts = null;
            }
            _currentSocket = null;
            await CloseSocketQuietlyAsync(socket);
        }

        return registered;
    }

    // Returns true if this message was a successful registration (HelloAck).
    private async Task<bool> HandleMessageAsync(IClientWebSocket socket, string json, CancellationToken token)
    {
        SocketMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<SocketMessage>(json);
        }
        catch (JsonException)
        {
            Log("Received malformed JSON from SocketWeb - ignored.");
            return false;
        }

        if (message is null)
        {
            return false;
        }

        switch (message.Type)
        {
            case MessageType.HelloAck:
                Log("Registered with SocketWeb as the Desktop AI client.");
                SetState(DesktopConnectionState.Connected);
                return true;

            case MessageType.AiRequest:
                await HandleAiRequestAsync(socket, message, token);
                return false;

            default:
                // Other message types aren't meaningful for this connection.
                return false;
        }
    }

    private async Task HandleAiRequestAsync(IClientWebSocket socket, SocketMessage request, CancellationToken token)
    {
        var requestId = request.RequestId;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            Log("Received an AiRequest without a RequestId - ignored.");
            return;
        }

        if (!_handledRequestIds.TryAdd(requestId, 0))
        {
            Log($"Ignoring duplicate AiRequest {ShortId(requestId)}.");
            return;
        }

        var shortId = ShortId(requestId);
        Log($"Received AiRequest {shortId} for session {ShortId(request.SessionId)}.");
        CurrentRequestChanged?.Invoke($"Processing {shortId}…");

        try
        {
            if (!_options.Ai.IsComplete)
            {
                await SendErrorAsync(socket, request, "The desktop AI client is not configured (missing base URL, model, or AI_API_KEY).", token);
                return;
            }

            using var timeoutCts = new CancellationTokenSource(_options.AiRequestTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            var history = request.History ?? new List<ConversationTurn>();
            var reply = await _aiClient.CompleteAsync(history, linkedCts.Token);

            await SendAsync(socket, new SocketMessage
            {
                Type = MessageType.AiResponse,
                SessionId = request.SessionId,
                RequestId = requestId,
                Content = reply,
                MessageRole = "assistant"
            }, token);

            Log($"Sent AiResponse for {shortId}.");
        }
        catch (AiAuthenticationException)
        {
            await SendErrorAsync(socket, request, "The AI API rejected the configured API key.", token);
        }
        catch (AiTimeoutException)
        {
            await SendErrorAsync(socket, request, "The AI API did not respond in time.", token);
        }
        catch (AiRequestException ex)
        {
            await SendErrorAsync(socket, request, $"The AI API returned an error (status {ex.StatusCode}).", token);
        }
        catch (AiInvalidResponseException)
        {
            await SendErrorAsync(socket, request, "The AI API returned an unexpected response.", token);
        }
        catch (OperationCanceledException)
        {
            // Connection dropped mid-request; nothing to send.
        }
        catch (Exception ex)
        {
            // Catch-all so one bad request can't take down the loop. Never
            // forwards raw exception details (paths, connection info) on.
            Log($"Unexpected error handling {shortId}: {ex.GetType().Name}");
            await SendErrorAsync(socket, request, "An unexpected error occurred while contacting the AI.", token);
        }
        finally
        {
            CurrentRequestChanged?.Invoke(null);
        }
    }

    private async Task SendErrorAsync(IClientWebSocket socket, SocketMessage request, string error, CancellationToken token)
    {
        Log($"Error for {ShortId(request.RequestId)}: {error}");
        await SendAsync(socket, new SocketMessage
        {
            Type = MessageType.Error,
            SessionId = request.SessionId,
            RequestId = request.RequestId,
            Error = error
        }, token);
    }

    private async Task SendAsync(IClientWebSocket socket, SocketMessage message, CancellationToken token)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var buffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

        // Serialized through a lock so two messages can never be sent on
        // the socket at once (WebSocket.SendAsync throws on overlap).
        await _sendLock.WaitAsync(token);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, endOfMessage: true, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"Failed to send to SocketWeb: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private TimeSpan NextBackoff(TimeSpan current)
    {
        var doubled = TimeSpan.FromTicks(current.Ticks * 2);
        return doubled > _options.MaxReconnectDelay ? _options.MaxReconnectDelay : doubled;
    }

    private static async Task CloseSocketQuietlyAsync(IClientWebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // Already gone - nothing to do.
        }
        finally
        {
            socket.Dispose();
        }
    }

    private void SetState(DesktopConnectionState state)
    {
        if (State == state)
        {
            return;
        }
        State = state;
        StateChanged?.Invoke(state);
    }

    private void Log(string message) => ActivityLogged?.Invoke(message);

    private static string ShortId(string? id) => string.IsNullOrEmpty(id) ? "?" : (id.Length <= 8 ? id : id[..8]);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sendLock.Dispose();
        _outerCts?.Dispose();
    }
}
