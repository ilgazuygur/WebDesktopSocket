using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace SocketWeb.Services;

// Keeps track of every open WebSocket connection (both browser tabs and the
// WPF desktop app connect the same way) and knows how to broadcast a message
// to all of them. This is the "hub" that makes the demo work.
public class WebSocketConnectionManager
{
    // ConcurrentDictionary is thread-safe, which matters because many
    // connections can be added/removed/broadcast to at the same time.
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();

    public string AddSocket(WebSocket socket)
    {
        var id = Guid.NewGuid().ToString();
        _sockets.TryAdd(id, socket);
        return id;
    }

    public void RemoveSocket(string id)
    {
        _sockets.TryRemove(id, out _);
    }

    // Sends the given text to every currently connected socket.
    // We broadcast to everyone (including the original sender) so that
    // each client only ever has ONE place it adds messages to its log:
    // the "message received" handler. This avoids showing duplicate
    // messages for the person who sent it.
    public async Task BroadcastAsync(string message)
    {
        var buffer = Encoding.UTF8.GetBytes(message);

        foreach (var (id, socket) in _sockets)
        {
            if (socket.State != WebSocketState.Open)
            {
                RemoveSocket(id);
                continue;
            }

            try
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None);
            }
            catch (Exception)
            {
                // If sending fails, the connection is probably dead already.
                RemoveSocket(id);
            }
        }
    }
}
