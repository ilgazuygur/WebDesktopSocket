using System.Text.Json.Serialization;

namespace SocketShared.Protocol;

// Identifies what kind of client is on the other end of a WebSocket
// connection. The server uses this to decide how to route messages:
// AI requests only ever go to a Desktop client, never to a Browser.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientRole
{
    Browser,
    Desktop
}
