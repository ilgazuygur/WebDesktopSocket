# Learning Notes

This document explains *how* this project works, for anyone learning
WebSockets, ASP.NET Core, or WPF for the first time.

## The big picture

There is only **one** WebSocket server in this whole system: `SocketWeb`.
Both the browser (JavaScript) and the desktop app (`SocketDesktop`) are
just *clients* that connect to it, the same way two people can call the
same phone number and both be on the line.

```
Browser  --ws://localhost:5080/ws-->  SocketWeb  <--ws://localhost:5080/ws--  SocketDesktop
 (JS WebSocket)                    (WebSocketConnectionManager)          (ClientWebSocket)
```

When either client sends a message, the server broadcasts it back out to
**every** connected client - including the one that sent it. That's the
key design decision that keeps this demo simple:

> The server is the single source of truth for "what messages exist".
> Neither client ever adds a message to its own log just because the user
> clicked Send - they only add a message when they *receive* one back from
> the server. This means there is exactly one code path for "show a
> message in the log", and no risk of the sender seeing their own message
> twice.

## SocketShared - the shared contract

`SocketShared/ChatMessage.cs` defines the one and only shape of a message:

```csharp
public class ChatMessage
{
    public string Sender { get; set; }
    public string Text { get; set; }
    public DateTime Timestamp { get; set; }
}
```

Both `SocketWeb` (server-side, in C#) and `SocketDesktop` (client-side, in
C#) reference this class directly and use `System.Text.Json` to convert it
to/from JSON. The browser's JavaScript can't reference a C# class, so
`socket-client.js` just builds a plain JS object with the exact same
property names (`Sender`, `Text`, `Timestamp`) - as long as the JSON looks
the same, it doesn't matter which language produced it.

## SocketWeb - the server

### Turning on WebSocket support

In `Program.cs`:

```csharp
app.UseWebSockets();
```

This one line tells ASP.NET Core's pipeline to know how to handle the
special HTTP request that upgrades a normal connection into a WebSocket
connection.

### The `/ws` endpoint

```csharp
app.Map("/ws", async (HttpContext context, WebSocketConnectionManager manager) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { ... }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    ...
});
```

This accepts the WebSocket handshake, then runs a loop:
`ReceiveAsync` (wait for a message) -> deserialize it -> `BroadcastAsync`
(send it to everyone). It loops forever until the client disconnects.

### WebSocketConnectionManager

A WebSocket connection, once open, isn't like a normal web request that
finishes quickly - it stays open for as long as the browser tab or the
desktop app is running. `WebSocketConnectionManager` (in
`Services/WebSocketConnectionManager.cs`) keeps a thread-safe dictionary
of every currently-open connection, and has one method,
`BroadcastAsync`, that loops through all of them and sends the same
message to each.

It's registered as a **singleton** (`AddSingleton`) so that there's
exactly one instance shared across all requests - if it were registered
normally (scoped/transient), every connection would get its own empty
dictionary and nobody would ever see anybody else's messages.

### The web page itself

`Pages/Index.cshtml` is a normal Razor Page - mostly just HTML with a
status div, an input box, a send button, and a log div. It loads
`wwwroot/js/socket-client.js`, which does all the actual WebSocket work
using the browser's built-in `WebSocket` class (no libraries needed).

## SocketDesktop - the WPF client

### ClientWebSocket

.NET's `System.Net.WebSockets.ClientWebSocket` is the client-side
counterpart to the server's WebSocket support. `SocketClientService`
wraps it with three simple operations:

- `ConnectAsync()` - connects to `ws://localhost:5080/ws`.
- `SendAsync(ChatMessage)` - serializes a message to JSON and sends it.
- A background "receive loop" that keeps calling `ReceiveAsync()` in a
  `while` loop for as long as the connection is open, raising the
  `MessageReceived` event each time a full message arrives.

### Talking back to the UI thread

WPF (like most UI frameworks) only allows you to touch UI controls (like
`ListBox` or `TextBlock`) from the one special "UI thread". But
`SocketClientService`'s receive loop runs on a background thread. That's
why `MainWindow.xaml.cs` wraps every UI update in:

```csharp
Dispatcher.Invoke(() => { ... });
```

`Dispatcher.Invoke` hops back onto the UI thread before touching
`LogList` or `StatusText`.

## Why raw WebSockets instead of SignalR?

SignalR is great for production apps (it adds automatic reconnection,
fallback transports, and a nicer RPC-style API), but it hides exactly the
mechanics this project is meant to teach: the WebSocket handshake, sending
raw JSON, and reading it back with a manual receive loop. Using
`System.Net.WebSockets` directly on both ends makes every step visible.

## Known simplifications (fine for a demo, not for production)

- Plain HTTP/`ws://`, not HTTPS/`wss://` - avoids dev-certificate setup.
- No reconnect-with-retry logic - if the connection drops, you must
  restart the app/refresh the page.
- No authentication - anyone who can reach `localhost:5080` can connect.
- Messages aren't persisted anywhere - closing everything loses the log.
