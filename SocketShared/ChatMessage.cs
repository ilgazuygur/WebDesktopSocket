namespace SocketShared;

// This is the ONE message shape that both the web page and the desktop app
// agree on. Both sides serialize/deserialize this exact class to/from JSON,
// so they can understand each other over the WebSocket connection.
public class ChatMessage
{
    // Who sent the message: "Web" or "Desktop".
    public string Sender { get; set; } = string.Empty;

    // The actual text the user typed.
    public string Text { get; set; } = string.Empty;

    // When the message was sent (used to show a timestamp in the log).
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
