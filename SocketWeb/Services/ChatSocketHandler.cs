using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SocketShared.Protocol;
using SocketWeb.Data;

namespace SocketWeb.Services;

// Turns incoming SocketMessage traffic into the right action: register a
// connection, save+route a user prompt to the desktop AI client, or
// save+route an AI reply (or error) back to the browser that asked for
// it. This is where the old "broadcast everything to everyone" demo
// behavior is replaced with real routing.
//
// Registered as a singleton (like WebSocketConnectionManager) because
// _pendingRequests must be shared across every connection for the whole
// lifetime of the app. Since EF Core's DbContext is scoped (and not
// thread-safe to share), this class creates a short-lived DI scope
// per-message whenever it needs IChatRepository, using IServiceScopeFactory
// - the same pattern ASP.NET Core's own background services use to reach
// scoped services from a singleton.
public class ChatSocketHandler
{
    // A prompt longer than this is almost certainly a mistake or abuse,
    // not a real chat message - reject it before it ever reaches the AI.
    private const int MaxPromptLength = 8000;

    private readonly WebSocketConnectionManager _connectionManager;
    private readonly IServiceScopeFactory _scopeFactory;

    // Keyed by RequestId. A request is "pending" from the moment its
    // UserPrompt is accepted until its AiResponse/Error arrives (or it's
    // rejected before ever reaching the desktop). Using
    // ConcurrentDictionary.TryAdd/TryRemove as the guard is what gives us
    // exactly-once processing: only one caller can ever successfully add
    // or remove a given RequestId.
    private readonly ConcurrentDictionary<string, PendingAiRequest> _pendingRequests = new();

    public ChatSocketHandler(WebSocketConnectionManager connectionManager, IServiceScopeFactory scopeFactory)
    {
        _connectionManager = connectionManager;
        _scopeFactory = scopeFactory;
    }

    public async Task HandleMessageAsync(string connectionId, string json, CancellationToken cancellationToken)
    {
        SocketMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<SocketMessage>(json);
        }
        catch (JsonException)
        {
            await SendErrorAsync(connectionId, requestId: null, "That message wasn't valid JSON.", cancellationToken);
            return;
        }

        if (message is null)
        {
            await SendErrorAsync(connectionId, requestId: null, "That message wasn't valid JSON.", cancellationToken);
            return;
        }

        var connection = _connectionManager.GetConnection(connectionId);
        if (connection is null)
        {
            // The connection already closed while this message was being
            // processed - nothing left to respond to.
            return;
        }

        // Every connection must register with ClientHello before anything
        // else is processed, so the server always knows which role it's
        // dealing with before trusting a message's content.
        if (message.Type != MessageType.ClientHello && connection.Role is null)
        {
            await SendErrorAsync(connectionId, message.RequestId, "Send ClientHello before other messages.", cancellationToken);
            return;
        }

        switch (message.Type)
        {
            case MessageType.ClientHello:
                await HandleClientHelloAsync(connectionId, message, cancellationToken);
                break;

            case MessageType.UserPrompt when connection.Role == ClientRole.Browser:
                await HandleUserPromptAsync(connectionId, message, cancellationToken);
                break;

            case MessageType.AiResponse when connection.Role == ClientRole.Desktop:
                await HandleAiResponseAsync(message, cancellationToken);
                break;

            case MessageType.Error when connection.Role == ClientRole.Desktop:
                await HandleDesktopErrorAsync(message, cancellationToken);
                break;

            default:
                // Not a message type this connection's role is allowed to
                // send (e.g. a browser trying to send an AiResponse) -
                // ignore it rather than trust it.
                break;
        }
    }

    private async Task HandleClientHelloAsync(string connectionId, SocketMessage message, CancellationToken cancellationToken)
    {
        if (message.Role is not (ClientRole.Browser or ClientRole.Desktop))
        {
            await SendErrorAsync(connectionId, message.RequestId, "ClientHello must specify Role as Browser or Desktop.", cancellationToken);
            return;
        }

        _connectionManager.SetRole(connectionId, message.Role.Value);

        var ack = new SocketMessage
        {
            Type = MessageType.HelloAck,
            Role = message.Role,
            // Echoed back purely for the client's own logging/display -
            // the server never trusts a client-supplied connection id for
            // routing decisions, only the one it generated itself.
            ConnectionId = connectionId,
            Content = _connectionManager.IsDesktopOnline ? SocketStatusCodes.DesktopOnline : SocketStatusCodes.DesktopOffline
        };

        await _connectionManager.SendAsync(connectionId, JsonSerializer.Serialize(ack), cancellationToken);
    }

    private async Task HandleUserPromptAsync(string connectionId, SocketMessage message, CancellationToken cancellationToken)
    {
        var requestId = message.RequestId;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            await SendErrorAsync(connectionId, requestId: null, "UserPrompt requires a RequestId.", cancellationToken);
            return;
        }

        if (!Guid.TryParse(message.SessionId, out var sessionGuid))
        {
            await SendErrorAsync(connectionId, requestId, "UserPrompt requires a valid SessionId.", cancellationToken);
            return;
        }

        var content = message.Content?.Trim();
        if (string.IsNullOrEmpty(content))
        {
            await SendErrorAsync(connectionId, requestId, "Prompt cannot be empty.", cancellationToken);
            return;
        }

        if (content.Length > MaxPromptLength)
        {
            await SendErrorAsync(connectionId, requestId, $"Prompt is too long (max {MaxPromptLength} characters).", cancellationToken);
            return;
        }

        // Idempotency guard: if this exact RequestId is already being
        // processed (e.g. the browser sent it twice), do nothing the
        // second time - in particular, never save the user message twice.
        var pending = new PendingAiRequest(message.SessionId!, connectionId);
        if (!_pendingRequests.TryAdd(requestId, pending))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatRepository>();

        var savedMessage = await repo.AddMessageAsync(sessionGuid, MessageRoles.User, content, cancellationToken);
        if (savedMessage is null)
        {
            // No such session - clean up the pending entry so this
            // RequestId isn't stuck "pending" forever, and so a corrected
            // retry with the same RequestId can succeed later.
            _pendingRequests.TryRemove(requestId, out _);
            await SendErrorAsync(connectionId, requestId, "That chat session no longer exists.", cancellationToken);
            return;
        }

        // Only this session's own messages, in order - this is what
        // guarantees the AI never sees another session's conversation.
        var history = await repo.GetMessagesAsync(sessionGuid, cancellationToken);

        var aiRequest = new SocketMessage
        {
            Type = MessageType.AiRequest,
            SessionId = message.SessionId,
            RequestId = requestId,
            History = history.Select(m => new ConversationTurn { Role = m.Role, Content = m.Content }).ToList()
        };

        var routed = await _connectionManager.SendToActiveDesktopAsync(JsonSerializer.Serialize(aiRequest), cancellationToken);
        if (!routed)
        {
            _pendingRequests.TryRemove(requestId, out _);
            await SendErrorAsync(connectionId, requestId, "The desktop AI client is offline. Try again once it reconnects.", cancellationToken);
            return;
        }

        var thinking = new SocketMessage
        {
            Type = MessageType.Status,
            SessionId = message.SessionId,
            RequestId = requestId,
            Content = SocketStatusCodes.Thinking
        };
        await _connectionManager.SendAsync(connectionId, JsonSerializer.Serialize(thinking), cancellationToken);
    }

    private async Task HandleAiResponseAsync(SocketMessage message, CancellationToken cancellationToken)
    {
        var requestId = message.RequestId;
        if (string.IsNullOrWhiteSpace(requestId) || !_pendingRequests.TryRemove(requestId, out var pending))
        {
            // Unknown, already-completed, or duplicate RequestId - ignore
            // safely rather than guessing what to do with it.
            return;
        }

        if (!string.Equals(message.SessionId, pending.SessionId, StringComparison.Ordinal))
        {
            // Doesn't match what we recorded when the request was
            // created - don't trust it. Put the pending entry back so a
            // legitimate response can still complete this request later.
            _pendingRequests.TryAdd(requestId, pending);
            return;
        }

        var content = message.Content?.Trim();
        if (string.IsNullOrEmpty(content))
        {
            await RouteErrorAsync(pending.BrowserConnectionId, requestId, "The AI response was empty.", cancellationToken);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatRepository>();

        // pending.SessionId was already validated as a Guid when the
        // UserPrompt created this pending request.
        var sessionGuid = Guid.Parse(pending.SessionId);
        var saved = await repo.AddMessageAsync(sessionGuid, MessageRoles.Assistant, content, cancellationToken);
        if (saved is null)
        {
            // The session was deleted (via the CRUD API) while the AI was
            // thinking - nothing sensible left to persist or route.
            return;
        }

        var response = new SocketMessage
        {
            Type = MessageType.AiResponse,
            SessionId = pending.SessionId,
            RequestId = requestId,
            Content = content,
            MessageRole = MessageRoles.Assistant
        };

        // Persistence above already happened regardless of whether the
        // browser is still connected - this send is best-effort. If it
        // fails (tab closed, refreshed, etc.) the message is still safely
        // in the database and reloads normally via GET /api/sessions/{id}.
        await _connectionManager.SendAsync(pending.BrowserConnectionId, JsonSerializer.Serialize(response), cancellationToken);
    }

    private async Task HandleDesktopErrorAsync(SocketMessage message, CancellationToken cancellationToken)
    {
        var requestId = message.RequestId;
        if (string.IsNullOrWhiteSpace(requestId) || !_pendingRequests.TryRemove(requestId, out var pending))
        {
            return;
        }

        // message.Error is expected to already be a short, safe summary
        // (SocketDesktop never forwards raw exception details or secrets)
        // - truncated defensively in case that ever changes.
        var safeError = Truncate(string.IsNullOrWhiteSpace(message.Error) ? "The AI request failed." : message.Error, 500);
        await RouteErrorAsync(pending.BrowserConnectionId, requestId, safeError, cancellationToken);
    }

    private async Task RouteErrorAsync(string connectionId, string requestId, string error, CancellationToken cancellationToken)
    {
        var errorMessage = new SocketMessage
        {
            Type = MessageType.Error,
            RequestId = requestId,
            Error = error
        };
        await _connectionManager.SendAsync(connectionId, JsonSerializer.Serialize(errorMessage), cancellationToken);
    }

    private Task<bool> SendErrorAsync(string connectionId, string? requestId, string error, CancellationToken cancellationToken)
    {
        var errorMessage = new SocketMessage
        {
            Type = MessageType.Error,
            RequestId = requestId,
            Error = error
        };
        return _connectionManager.SendAsync(connectionId, JsonSerializer.Serialize(errorMessage), cancellationToken);
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    // SessionId/BrowserConnectionId recorded at the moment a UserPrompt is
    // accepted, so the eventual AiResponse/Error can be routed back to
    // exactly the right browser tab and validated against the right
    // session - never trusting whatever the desktop's response claims.
    private sealed record PendingAiRequest(string SessionId, string BrowserConnectionId);
}
