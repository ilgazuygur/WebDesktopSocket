using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SocketShared.Ai;
using SocketShared.Protocol;

namespace SocketDesktop.Services;

// Connects to SocketWeb's /ws endpoint as the AI desktop client: registers
// with ClientHello/ClientRole.Desktop, then handles every AiRequest it
// receives by calling IAiClient and replying with AiResponse on success,
// or Error for any failure (auth, timeout, provider, malformed response).
// This is the only place in the whole solution that calls IAiClient.
public sealed class DesktopSocketClient : IAsyncDisposable
{
    // Same fixed URL SocketWeb listens on (see SocketWeb/Program.cs).
    private static readonly Uri ServerUri = new("ws://localhost:5080/ws");

    // How long a single AI call is allowed to take before this client
    // gives up and reports a timeout Error - independent of whatever
    // HttpClient.Timeout is configured with, so a hung request can never
    // leave a browser waiting forever.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly IAiClient _aiClient;
    private readonly AiOptions _aiOptions;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveLoopCts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Guards against handling the same AiRequest twice (e.g. a message
    // redelivered around a reconnect). Never cleared - acceptable for a
    // single desktop-session's lifetime; a long-running production
    // service would want to trim old entries.
    private readonly ConcurrentDictionary<string, byte> _handledRequestIds = new();

    public event Action<bool>? ConnectionStatusChanged;
    public event Action<string>? ActivityLogged;

    // Null means idle; otherwise a short human-readable description of
    // the request currently being processed.
    public event Action<string?>? CurrentRequestChanged;

    public DesktopSocketClient(IAiClient aiClient, AiOptions aiOptions)
    {
        _aiClient = aiClient;
        _aiOptions = aiOptions;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await CloseCurrentSocketAsync();

        _socket = new ClientWebSocket();

        try
        {
            await _socket.ConnectAsync(ServerUri, cancellationToken);
            Log("Connected to SocketWeb.");

            await SendAsync(new SocketMessage { Type = MessageType.ClientHello, Role = ClientRole.Desktop }, cancellationToken);

            _receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = ReceiveLoopAsync(_receiveLoopCts.Token);
        }
        catch (Exception ex)
        {
            Log($"Could not connect to SocketWeb: {ex.Message}");
            ConnectionStatusChanged?.Invoke(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];

        try
        {
            while (_socket is { State: WebSocketState.Open })
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

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
                await HandleMessageAsync(json, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when ConnectAsync (reconnect) or DisposeAsync cancels this loop.
        }
        catch (Exception ex)
        {
            Log($"Connection to SocketWeb lost: {ex.Message}");
        }
        finally
        {
            ConnectionStatusChanged?.Invoke(false);
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken cancellationToken)
    {
        SocketMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<SocketMessage>(json);
        }
        catch (JsonException)
        {
            Log("Received malformed JSON from SocketWeb - ignored.");
            return;
        }

        if (message is null)
        {
            return;
        }

        switch (message.Type)
        {
            case MessageType.HelloAck:
                Log("Registered with SocketWeb as the Desktop AI client.");
                ConnectionStatusChanged?.Invoke(true);
                break;

            case MessageType.AiRequest:
                await HandleAiRequestAsync(message, cancellationToken);
                break;

            default:
                // Status/Error/ClientHello/UserPrompt/AiResponse are not
                // meaningful for this connection to receive - ignore.
                break;
        }
    }

    private async Task HandleAiRequestAsync(SocketMessage request, CancellationToken cancellationToken)
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
        CurrentRequestChanged?.Invoke($"Processing {shortId}...");

        try
        {
            if (!_aiOptions.IsComplete)
            {
                await SendErrorAsync(request, "The desktop AI client is not configured (missing base URL, model, or AI_API_KEY).", cancellationToken);
                return;
            }

            using var timeoutCts = new CancellationTokenSource(RequestTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var history = request.History ?? new List<ConversationTurn>();
            var reply = await _aiClient.CompleteAsync(history, linkedCts.Token);

            await SendAsync(new SocketMessage
            {
                Type = MessageType.AiResponse,
                SessionId = request.SessionId,
                RequestId = requestId,
                Content = reply,
                MessageRole = "assistant"
            }, cancellationToken);

            Log($"Sent AiResponse for {shortId}.");
        }
        catch (AiAuthenticationException)
        {
            await SendErrorAsync(request, "The AI API rejected the configured API key.", cancellationToken);
        }
        catch (AiTimeoutException)
        {
            await SendErrorAsync(request, "The AI API did not respond in time.", cancellationToken);
        }
        catch (AiRequestException ex)
        {
            await SendErrorAsync(request, $"The AI API returned an error (status {ex.StatusCode}).", cancellationToken);
        }
        catch (AiInvalidResponseException)
        {
            await SendErrorAsync(request, "The AI API returned an unexpected response.", cancellationToken);
        }
        catch (Exception ex)
        {
            // Catch-all so one bad request can never take down the receive
            // loop. Deliberately generic - never forwards raw exception
            // details (which could include internal paths, connection
            // info, etc.) to the browser.
            Log($"Unexpected error handling {shortId}: {ex.GetType().Name}");
            await SendErrorAsync(request, "An unexpected error occurred while contacting the AI.", cancellationToken);
        }
        finally
        {
            CurrentRequestChanged?.Invoke(null);
        }
    }

    private async Task SendErrorAsync(SocketMessage request, string error, CancellationToken cancellationToken)
    {
        Log($"Error for {ShortId(request.RequestId)}: {error}");
        await SendAsync(new SocketMessage
        {
            Type = MessageType.Error,
            SessionId = request.SessionId,
            RequestId = request.RequestId,
            Error = error
        }, cancellationToken);
    }

    private async Task SendAsync(SocketMessage message, CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open })
        {
            return;
        }

        var json = JsonSerializer.Serialize(message);
        var buffer = Encoding.UTF8.GetBytes(json);

        // Serialized through a lock, same as SocketWeb's connection manager,
        // so two messages can never be sent on this socket at once.
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
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

    private async Task CloseCurrentSocketAsync()
    {
        _receiveLoopCts?.Cancel();
        _receiveLoopCts?.Dispose();
        _receiveLoopCts = null;

        if (_socket is null)
        {
            return;
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // Already gone - nothing to do.
        }
        finally
        {
            _socket.Dispose();
            _socket = null;
        }
    }

    private void Log(string message) => ActivityLogged?.Invoke(message);

    private static string ShortId(string? id) => string.IsNullOrEmpty(id) ? "?" : (id.Length <= 8 ? id : id[..8]);

    public async ValueTask DisposeAsync()
    {
        await CloseCurrentSocketAsync();
        _sendLock.Dispose();
    }
}
