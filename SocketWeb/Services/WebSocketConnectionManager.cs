using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SocketShared.Protocol;

namespace SocketWeb.Services;

// Tracks every open WebSocket connection (browser tabs and the desktop
// app all connect the same way) along with its server-assigned
// id and role, and knows how to safely send to one specific connection -
// this is what makes routing (instead of the old "broadcast to
// everyone") possible.
//
// Registered as a singleton: there is exactly one instance for the whole
// application, so every request/connection shares the same view of "who
// is currently connected".
public class WebSocketConnectionManager
{
    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();

    // Which connection (if any) is the current "active" AI desktop
    // client. Guarded by _desktopLock rather than relying on plain
    // reference assignment, so "swap the active desktop and decide
    // whether to notify browsers" happens as one atomic step.
    private readonly object _desktopLock = new();
    private string? _activeDesktopConnectionId;

    public bool IsDesktopOnline => _activeDesktopConnectionId is not null;

    // Accepts a newly-connected socket and gives it a server-generated
    // id. The server is the only source of truth for connection ids -
    // nothing a client sends is ever trusted as its own connection id.
    public string AddConnection(WebSocket socket)
    {
        var connectionId = Guid.NewGuid().ToString();
        _connections[connectionId] = new ConnectionInfo(connectionId, socket);
        return connectionId;
    }

    public ConnectionInfo? GetConnection(string connectionId)
    {
        return _connections.TryGetValue(connectionId, out var info) ? info : null;
    }

    // Called once a connection's ClientHello has been received. If it
    // registers as the Desktop AI client, it immediately becomes "the"
    // active desktop client (most-recently-registered wins), and every
    // connected browser is told the desktop is now online.
    public void SetRole(string connectionId, ClientRole role)
    {
        if (!_connections.TryGetValue(connectionId, out var info))
        {
            return;
        }

        info.Role = role;

        if (role == ClientRole.Desktop)
        {
            lock (_desktopLock)
            {
                _activeDesktopConnectionId = connectionId;
            }

            _ = NotifyDesktopStatusChangedAsync(online: true);
        }
    }

    // Sends JSON to exactly one connection, serialized through that
    // connection's own send lock. Returns false (rather than throwing) if
    // the connection doesn't exist, is no longer open, or sending fails -
    // callers treat "couldn't deliver" as a normal, expected outcome
    // (e.g. the browser tab it was meant for already closed).
    public async Task<bool> SendAsync(string connectionId, string json, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(connectionId, out var info))
        {
            return false;
        }

        if (info.Socket.State != WebSocketState.Open)
        {
            return false;
        }

        var buffer = Encoding.UTF8.GetBytes(json);

        await info.SendLock.WaitAsync(cancellationToken);
        try
        {
            if (info.Socket.State != WebSocketState.Open)
            {
                return false;
            }

            await info.Socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            // The connection is probably dead already - the receive loop
            // for this socket will notice and call RemoveConnectionAsync.
            return false;
        }
        finally
        {
            info.SendLock.Release();
        }
    }

    // Sends to whichever connection is currently the active desktop
    // client. Returns false if no desktop is connected at all, so callers
    // can respond with a clear "desktop offline" error instead of
    // silently dropping the request.
    public Task<bool> SendToActiveDesktopAsync(string json, CancellationToken cancellationToken = default)
    {
        var desktopId = _activeDesktopConnectionId;
        return desktopId is null ? Task.FromResult(false) : SendAsync(desktopId, json, cancellationToken);
    }

    // Called when a connection's receive loop ends (client disconnected,
    // closed, or errored). Cleans up its tracking state and, if it was
    // the active desktop client, tells every browser it's now offline.
    public async Task RemoveConnectionAsync(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var info))
        {
            return;
        }

        bool wasActiveDesktop;
        lock (_desktopLock)
        {
            wasActiveDesktop = _activeDesktopConnectionId == connectionId;
            if (wasActiveDesktop)
            {
                _activeDesktopConnectionId = null;
            }
        }

        info.SendLock.Dispose();

        try
        {
            if (info.Socket.State == WebSocketState.Open)
            {
                await info.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed", CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // Already gone - nothing to do.
        }
        finally
        {
            info.Socket.Dispose();
        }

        if (wasActiveDesktop)
        {
            await NotifyDesktopStatusChangedAsync(online: false);
        }
    }

    private async Task NotifyDesktopStatusChangedAsync(bool online)
    {
        var status = new SocketMessage
        {
            Type = MessageType.Status,
            Content = online ? SocketStatusCodes.DesktopOnline : SocketStatusCodes.DesktopOffline
        };
        var json = JsonSerializer.Serialize(status);

        foreach (var info in _connections.Values)
        {
            if (info.Role == ClientRole.Browser)
            {
                await SendAsync(info.ConnectionId, json);
            }
        }
    }
}
