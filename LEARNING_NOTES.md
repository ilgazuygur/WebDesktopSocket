# Learning Notes

This document explains *how* this project works, for anyone learning
WebSockets, ASP.NET Core, EF Core, or WPF for the first time.

## The big picture

There is only **one** WebSocket server in this whole system: `SocketWeb`.
The browser (JavaScript) and the desktop app (`SocketDesktop`) are both
just *clients* that connect to it - but unlike a simple chat demo, they
are **different kinds** of client, and the server treats them
differently on purpose:

```
Browser  --ws://localhost:5080/ws-->  SocketWeb  <--ws://localhost:5080/ws--  SocketDesktop
 (JS WebSocket,                    (ChatSocketHandler +                    (ClientWebSocket,
  ClientRole.Browser)               WebSocketConnectionManager)             ClientRole.Desktop)
                                            │
                                            ▼
                                          MySQL
```

When a browser sends a prompt, the server does **not** broadcast it to
everyone. It saves it, looks up the *one* connected desktop client, and
routes an `AiRequest` to *only* that connection. When the desktop replies,
the server routes the answer back to *only* the browser that asked. This
is the key difference from a typical "everyone sees everything" WebSocket
demo, and it's what makes chat sessions actually private to the person
using them.

## Why the desktop app is in the loop at all

The most important design decision in this whole project: **the browser
never calls the AI API, and never sees the AI API key.**

If the browser called the AI API directly, the API key would have to live
in JavaScript - and anything in JavaScript is visible to anyone who opens
their browser's dev tools. So instead:

```
Browser --(WebSocket, no secrets)--> SocketWeb --(WebSocket, no secrets)--> SocketDesktop --(HTTPS, has the key)--> AI API
```

The API key only ever exists in one place: `SocketDesktop`'s process
memory (loaded from the `AI_API_KEY` environment variable). Neither
`SocketWeb` nor the browser ever has it.

## SocketShared - the shared contract

### The WebSocket protocol

`SocketShared/Protocol/SocketMessage.cs` defines the one envelope every
WebSocket message uses - browser-to-server and server-to-desktop alike:

```csharp
public class SocketMessage
{
    public MessageType Type { get; set; }       // ClientHello, UserPrompt, AiRequest, ...
    public ClientRole? Role { get; set; }        // set on ClientHello
    public string? SessionId { get; set; }
    public string? RequestId { get; set; }        // correlates a request with its eventual reply
    public string? ConnectionId { get; set; }
    public string? Content { get; set; }
    public string? MessageRole { get; set; }      // "user" / "assistant" / "system"
    public List<ConversationTurn>? History { get; set; }
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; }
}
```

Not every field is used by every `MessageType` - a `UserPrompt` sets
`SessionId`/`RequestId`/`Content`; an `AiRequest` additionally carries
`History`; an `Error` sets `Error` instead of `Content`. Both `SocketWeb`
(C#) and `SocketDesktop` (C#) reference this exact class. The browser's
JavaScript can't reference a C# class, so it just builds plain JS objects
with the same property names (`Type`, `SessionId`, ...) - as long as the
JSON looks the same, it doesn't matter which language produced it.

`RequestId` is the single most important field for correctness: it's how
the server knows which browser to route an eventual `AiResponse` back to,
and it's the *idempotency key* that guarantees a message never gets saved
twice even if it's somehow delivered twice.

### The AI client abstraction

`SocketShared/Ai/IAiClient.cs` is one method:

```csharp
Task<string> CompleteAsync(IReadOnlyList<ConversationTurn> messages, CancellationToken ct = default);
```

`OpenAiCompatibleClient` is the one implementation, using a plain
`HttpClient` to POST to `{BaseUrl}/chat/completions` - the same request
shape OpenAI, OpenRouter, and many local model servers all understand.
Because the rest of the app only ever talks to the `IAiClient` interface,
switching providers is a configuration change (`Ai:BaseUrl`, `Ai:Model`,
`AI_API_KEY`), not a code change.

## SocketWeb - the server

### Turning on WebSocket support

In `Program.cs`:

```csharp
app.UseWebSockets();
```

This tells ASP.NET Core's pipeline how to handle the special HTTP request
that upgrades a normal connection into a WebSocket connection.

### The `/ws` endpoint: framing only

```csharp
app.Map("/ws", async (HttpContext context, WebSocketConnectionManager manager, ChatSocketHandler handler) =>
{
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connectionId = manager.AddConnection(socket);
    // ... loop: read frames until a full message arrives (or the 256 KB
    // cap is hit), then hand the JSON text to handler.HandleMessageAsync ...
});
```

This delegate's only job is turning WebSocket *frames* into complete
*messages* (a single logical message can arrive across more than one
frame) and enforcing a maximum message size, so a buggy or malicious
client can't make the server buffer unlimited memory for one message. All
the actual decision-making about *what to do* with a message lives
elsewhere, in `ChatSocketHandler` - keeping this loop simple and testable
independently.

### WebSocketConnectionManager: who is connected, and how to reach them

`Services/WebSocketConnectionManager.cs` keeps a thread-safe dictionary of
every open connection - its server-generated id (never trusted from the
client), its role (`Browser`/`Desktop`, set once `ClientHello` arrives),
and a per-connection `SemaphoreSlim`. That lock matters: `WebSocket.SendAsync`
throws if two calls overlap on the same connection, so every send goes
through `WebSocketConnectionManager.SendAsync`, which acquires that lock
first. It also tracks which connection is the *active* desktop client
(the most recently registered one wins) and notifies every browser when
that flips online/offline.

It's a **singleton** - one instance for the whole app - so every request
and every connection shares the same view of "who is connected right now".

### ChatSocketHandler: the actual routing logic

`Services/ChatSocketHandler.cs` is where a `UserPrompt` becomes a saved
database row, a routed `AiRequest`, and (eventually) a saved, routed
`AiResponse`. The key trick for "never save the same message twice" is a
`ConcurrentDictionary<string RequestId, PendingAiRequest>`:

```csharp
if (!_pendingRequests.TryAdd(requestId, pending)) { return; } // already being handled - ignore
```

`TryAdd` is atomic, so only the *first* caller with a given `RequestId`
ever proceeds to save the message and forward it. When the matching
`AiResponse` arrives, `TryRemove` is the same trick in reverse - only the
first `AiResponse` for a `RequestId` is processed; any later one (a
duplicate, or a stray message with an unknown id) is safely ignored.

Because this class is a singleton but needs `IChatRepository` (backed by
EF Core's `DbContext`, which is *not* safe to share across concurrent
work), it doesn't take `IChatRepository` directly - it takes
`IServiceScopeFactory` and creates a short-lived DI scope for each message
that needs the database. This is the same pattern ASP.NET Core's own
background services use to reach scoped services from a singleton.

### Persistence (EF Core + MySQL)

`SocketWeb/Data/ChatDbContext.cs` maps two entities - `ChatSession` and
`ChatMessage` - with a cascade-delete foreign key (deleting a session
deletes its messages) and an index on `(SessionId, Sequence)`, since
"load this session's messages in order" is the most common query.
`ChatRepository.cs` is the only place that talks to `ChatDbContext`
directly; everything else (the WebSocket handler, the REST endpoints)
goes through `IChatRepository`.

`Sequence` (not `CreatedAt`) is what guarantees message order - assigning
it inside the same method that saves the message avoids any dependency on
clock precision.

### The REST API (`/api/sessions`)

`SocketWeb/Api/SessionEndpoints.cs` is plain ASP.NET Core minimal API
CRUD - no WebSocket involved. DTOs (`SocketWeb/Api/Dtos.cs`) are kept
separate from the EF Core entities on purpose: the JSON shape the browser
sees shouldn't change just because a database column does, and entities
shouldn't leak EF Core navigation properties into a JSON response.

### The web page and its JavaScript

`Pages/Index.cshtml` is a Razor Page shell; almost everything happens in
`wwwroot/js/`:

- **`api.js`** - `fetch()` wrappers around `/api/sessions`.
- **`ws.js`** - owns the one `WebSocket` connection: connects, sends
  `ClientHello`, and reconnects with exponential backoff if the
  connection drops (doubling the delay each time, capped at 30s).
- **`chat.js`** - everything else: rendering the sidebar and messages,
  the composer, and reacting to incoming `SocketMessage`s.

One detail worth calling out: message content is *always* set via
`element.textContent`, never `element.innerHTML`. That's what makes it
safe to display anything a user (or the AI) sends, including something
that looks like `<script>...</script>` - the browser renders it as
literal text instead of executing it. Line breaks are preserved safely
too, via CSS (`white-space: pre-wrap`) rather than converting `\n` into
`<br>` HTML, which would have reintroduced the same risk.

## SocketDesktop - the WPF client

### DesktopSocketClient

`Services/DesktopSocketClient.cs` is the WPF-side counterpart to
`ChatSocketHandler` - though structurally simpler, since it only has to
handle one message type that matters (`AiRequest`):

```csharp
await SendAsync(new SocketMessage { Type = MessageType.ClientHello, Role = ClientRole.Desktop }, ct);
// ... receive loop ...
// on AiRequest: call IAiClient.CompleteAsync, then send AiResponse (success)
//               or Error (auth failure / timeout / provider error / malformed response)
```

It uses the same two tricks `SocketWeb`'s side does, for the same
reasons: a `SemaphoreSlim` around every send (so two replies can never
be written to the socket at once), and a `ConcurrentDictionary` of
already-handled `RequestId`s (so a redelivered `AiRequest`, e.g. around a
reconnect, is never processed twice).

### Generic Host, dependency injection, and configuration

`App.xaml.cs` builds a `Microsoft.Extensions.Hosting` generic host - the
same DI/configuration/lifetime system ASP.NET Core apps use - instead of
`new`-ing everything up by hand. Configuration comes from
`appsettings.json` (`Ai:BaseUrl`, `Ai:Model` - non-secret) plus the
`AI_API_KEY` environment variable (read directly, not through the
`Ai:*` config path, so its name matches exactly what's documented).
`MainWindow` itself is resolved from the DI container, so its
constructor can simply ask for the `DesktopSocketClient` and `AiOptions`
it needs.

### Talking back to the UI thread

WPF (like most UI frameworks) only allows you to touch UI controls (like
`TextBlock`) from the one special "UI thread". But `DesktopSocketClient`'s
events (`ConnectionStatusChanged`, `ActivityLogged`, ...) can fire from a
background thread (the WebSocket receive loop). That's why every handler
in `MainWindow.xaml.cs` wraps its work in:

```csharp
Dispatcher.Invoke(() => { ... });
```

`Dispatcher.Invoke` hops back onto the UI thread before touching anything
on screen.

### What the WPF window actually shows

Since the chat UI lives entirely on the web, `SocketDesktop`'s window is
an **operational dashboard**: WebSocket connection status, registration
status, AI configuration status (explicitly never the key itself - only
"Configured" or "Missing"), what request (if any) is being processed
right now, and a short activity log.

## Why raw WebSockets instead of SignalR?

SignalR is great for production apps (it adds automatic reconnection,
fallback transports, and a nicer RPC-style API), but it hides exactly the
mechanics this project is meant to teach: the WebSocket handshake, sending
raw JSON, and reading it back with a manual receive loop. Using
`System.Net.WebSockets` directly on both ends makes every step visible -
including the parts (per-connection send locks, manual reconnect,
message framing) that a library like SignalR would otherwise do for you
invisibly.

## How the tests avoid needing Windows, MySQL, or a real AI API

- **Protocol JSON** and the **AI HTTP client** need nothing external - the
  AI client is tested against a stubbed `HttpMessageHandler` that never
  makes a real network call.
- **Persistence and the REST API** run against EF Core's **InMemory**
  provider - same repository code and LINQ queries as production, just a
  different (non-MySQL) storage engine underneath.
- **WebSocket routing** is tested two ways: fast unit tests drive
  `ChatSocketHandler` directly against a `FakeWebSocket` test double (a
  minimal hand-written `WebSocket` subclass that reproduces the real
  "two concurrent `SendAsync` calls throw" failure, so the send-lock fix
  is genuinely exercised, not just assumed), and slower but more
  realistic integration tests drive the *actual* `/ws` endpoint through
  `WebApplicationFactory` + `TestServer`'s in-memory WebSocket client -
  including one test that opens two real WebSocket connections (one
  playing the browser, one playing a "fake desktop") and proves the whole
  `UserPrompt -> AiRequest -> AiResponse` round trip is routed and
  persisted correctly.
- **`SocketDesktop` itself** (the actual WPF process) can't be exercised
  this way - a `net8.0-windows`/WPF assembly can't load on macOS/Linux
  even with cross-build enabled. Its logic is covered indirectly (the AI
  client it calls is tested directly; the protocol/routing behavior it
  participates in is tested via the fake-desktop-client tests above), but
  the real WPF UI and process genuinely need the Windows checklist in
  README.md.

## Known simplifications (fine for a learning project, not for production)

- Plain HTTP/`ws://` between the browser/desktop and `SocketWeb`, not
  HTTPS/`wss://` - avoids dev-certificate setup. (`SocketDesktop`'s call
  to the *AI API* itself **is** HTTPS - that boundary is the one that
  actually crosses the public internet.)
- No user authentication - anyone who can reach `localhost:5080` can use
  any session. Fine for a single-user local demo; a real deployment would
  need this.
- Full conversation history is sent to the AI on every request, with no
  truncation for very long sessions - simplest to reason about, but could
  hit a provider's context-length limit on an extremely long chat.
- "Most-recently-registered desktop wins" - if two `SocketDesktop`
  instances connect at once, only the newest one receives `AiRequest`s.
  Simple and predictable, but not a queueing/load-balancing story.
- The WebSocket `_pendingRequests`/`_handledRequestIds` dictionaries (on
  both `SocketWeb` and `SocketDesktop`) are never trimmed - fine for a
  single long-running session, but a true production service would want
  to expire old entries.
