using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SocketShared;

namespace SocketDesktop.Services;

// Wraps a ClientWebSocket so the rest of the app doesn't need to know
// anything about WebSockets or JSON - it just calls ConnectAsync/SendAsync
// and listens to the two events below.
public class SocketClientService
{
    // Same fixed URL that SocketWeb listens on (see SocketWeb/Program.cs).
    private static readonly Uri ServerUri = new("ws://localhost:5080/ws");

    private ClientWebSocket? _socket;

    // Raised whenever a message (from the web page or this app) arrives.
    public event Action<ChatMessage>? MessageReceived;

    // Raised whenever we connect or disconnect, so the UI can update its
    // status label.
    public event Action<bool>? ConnectionStatusChanged;

    public async Task ConnectAsync()
    {
        _socket = new ClientWebSocket();

        try
        {
            await _socket.ConnectAsync(ServerUri, CancellationToken.None);
            ConnectionStatusChanged?.Invoke(true);

            // Start listening for incoming messages in the background,
            // without blocking whoever called ConnectAsync.
            _ = ReceiveLoopAsync();
        }
        catch (Exception)
        {
            ConnectionStatusChanged?.Invoke(false);
        }
    }

    public async Task SendAsync(ChatMessage message)
    {
        if (_socket is not { State: WebSocketState.Open })
        {
            return;
        }

        var json = JsonSerializer.Serialize(message);
        var buffer = Encoding.UTF8.GetBytes(json);

        await _socket.SendAsync(
            new ArraySegment<byte>(buffer),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    // Runs for as long as the connection is open, reading one message at a
    // time and raising MessageReceived for each one.
    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];

        try
        {
            while (_socket is { State: WebSocketState.Open })
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var message = JsonSerializer.Deserialize<ChatMessage>(json);

                if (message is not null)
                {
                    MessageReceived?.Invoke(message);
                }
            }
        }
        catch (Exception)
        {
            // The connection was dropped (e.g. SocketWeb was stopped).
        }
        finally
        {
            ConnectionStatusChanged?.Invoke(false);
        }
    }
}
