using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SocketShared.Protocol;

namespace Socket.Tests.TestInfrastructure;

// Small helpers shared by tests that drive the real /ws endpoint through
// TestServer's in-memory WebSocket client (real framing/JSON, no actual
// network socket).
internal static class WebSocketTestHelpers
{
    public static Task SendTextAsync(WebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    public static Task SendMessageAsync(WebSocket socket, SocketMessage message)
    {
        return SendTextAsync(socket, JsonSerializer.Serialize(message));
    }

    public static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    public static async Task<SocketMessage> ReceiveMessageAsync(WebSocket socket)
    {
        return JsonSerializer.Deserialize<SocketMessage>(await ReceiveTextAsync(socket))!;
    }
}
