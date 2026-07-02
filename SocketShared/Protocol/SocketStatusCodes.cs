namespace SocketShared.Protocol;

// Well-known values used in SocketMessage.Content when Type is
// MessageType.Status. Kept as plain strings (rather than a fourth enum)
// so Status can still carry free-form human-readable text for anything
// not listed here, while both SocketWeb and the browser's JS agree on
// these specific, important state changes.
public static class SocketStatusCodes
{
    // Sent to all connected browsers when the (single) active Desktop AI
    // client connects/registers, or disconnects.
    public const string DesktopOnline = "desktop-online";
    public const string DesktopOffline = "desktop-offline";

    // Sent to the requesting browser right after its UserPrompt has been
    // routed to the desktop client, so the UI can show "AI is thinking".
    public const string Thinking = "thinking";
}
