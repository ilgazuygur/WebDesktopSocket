using System.Text.Json.Serialization;

namespace SocketShared.Protocol;

// Every message that travels over the /ws WebSocket connection is tagged
// with one of these types, so both sides know how to interpret it.
// [JsonStringEnumConverter] makes this serialize as the readable string
// "UserPrompt" instead of a number, which makes raw WebSocket traffic
// much easier to read while debugging.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageType
{
    // Sent by a client right after connecting, to say who it is.
    ClientHello,

    // Sent by the server in reply to ClientHello, to confirm registration.
    HelloAck,

    // Sent by a browser client: the text the user typed.
    UserPrompt,

    // Sent by the server to the desktop client: "please ask the AI this,
    // here is the conversation history so far".
    AiRequest,

    // Sent by the desktop client back to the server: the AI's answer.
    AiResponse,

    // Sent by the server to a browser client: a status update, e.g.
    // "AI is thinking" or "desktop client offline".
    Status,

    // Sent by either side when something goes wrong.
    Error
}
