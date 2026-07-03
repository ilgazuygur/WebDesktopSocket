namespace SocketDesktop.Core;

// The lifecycle states the desktop client moves through. The UI binds to
// this to show a clear, single status instead of juggling several booleans.
public enum DesktopConnectionState
{
    // Not connected and not trying to (initial state, or after a graceful Stop).
    Disconnected,

    // A connection attempt is in progress (first attempt).
    Connecting,

    // Connected AND registered with SocketWeb as the Desktop client
    // (i.e. HelloAck received) - the only state in which AiRequests flow.
    Connected,

    // Was connected/attempted, lost it, and is waiting to try again
    // (backoff) or actively retrying.
    Reconnecting
}
