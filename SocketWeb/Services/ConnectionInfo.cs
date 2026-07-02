using System.Net.WebSockets;
using SocketShared.Protocol;

namespace SocketWeb.Services;

// Everything WebSocketConnectionManager tracks about one open WebSocket:
// the socket itself, the server-generated id that identifies it, which
// kind of client it is (null until its ClientHello arrives), and a lock
// that makes sure only one SendAsync call is ever in flight on this
// socket at a time (WebSocket.SendAsync throws if called concurrently
// from two places on the same instance).
public class ConnectionInfo
{
    public ConnectionInfo(string connectionId, WebSocket socket)
    {
        ConnectionId = connectionId;
        Socket = socket;
    }

    public string ConnectionId { get; }
    public WebSocket Socket { get; }

    // Set once the connection's ClientHello is processed. Null means "not
    // registered yet" - messages other than ClientHello are rejected
    // until this is set (see ChatSocketHandler).
    public ClientRole? Role { get; set; }

    public SemaphoreSlim SendLock { get; } = new(1, 1);
}
