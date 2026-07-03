# Learning Notes

This document explains *how* this project works, for anyone learning
WebSockets, ASP.NET Core, EF Core, or WPF for the first time.

## Sockets and WebSockets, from scratch

**What is a socket?** A socket is one end of a two-way communication link
between two programs over a network. Think of it like a telephone: once
both ends "pick up", either side can talk, and the line stays open until
someone hangs up. This is different from a plain website request, where
your browser asks for a page, gets it, and the conversation is over.

**What is a WebSocket?** Normal web traffic (HTTP) is request/response:
the browser asks, the server answers, done. That's awkward for a chat app,
because the server often needs to *push* something to the browser when the
browser didn't ask for anything (e.g. "the AI just replied"). A WebSocket
solves this: it starts as a normal HTTP request, then "upgrades" into a
persistent, two-way connection that stays open. After the upgrade, either
side can send a message to the other at any time, instantly - no polling,
no re-connecting per message.

**Why raw WebSockets here?** .NET and browsers both have built-in
WebSocket support (`System.Net.WebSockets` on the server/desktop, the
`WebSocket` class in the browser). There are also higher-level libraries -
most famously **SignalR** - that wrap WebSockets and add features like
automatic reconnection and a nicer API. This project deliberately uses the
**raw** WebSocket APIs on all three sides instead, because the whole point
is to *learn* the mechanics: the handshake, sending JSON by hand, reading
it back in a receive loop, reconnecting, and framing messages. SignalR
would hide exactly the parts worth understanding.

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

### The message types (what each one means)

`MessageType` (in `SocketShared/Protocol/MessageType.cs`) is the "verb" of
every message. Here's each one, and who sends it to whom:

- **`ClientHello`** (client -> server): the very first message a client
  sends after connecting, saying which kind of client it is
  (`Role = Browser` or `Role = Desktop`). Until this arrives, the server
  won't process anything else from that connection.
- **`HelloAck`** (server -> client): the server's reply to `ClientHello`,
  confirming registration and echoing back the server-assigned
  `ConnectionId`.
- **`UserPrompt`** (browser -> server): "the user typed this" - carries
  the `SessionId`, a fresh `RequestId`, and the prompt `Content`.
- **`AiRequest`** (server -> desktop): "please ask the AI this" - the same
  `SessionId`/`RequestId`, plus the conversation `History` so the AI has
  context. Sent to the desktop client *only*, never broadcast.
- **`AiResponse`** (desktop -> server -> browser): the AI's answer,
  carrying the same `RequestId` so the server can route it back to exactly
  the browser that asked.
- **`Status`** (server -> browser): a state update, e.g. `thinking`
  ("AI is working on it") or `desktop-online` / `desktop-offline`.
- **`Error`** (any direction): something went wrong - carries a short,
  safe `Error` message and (when relevant) the `RequestId` it relates to.

**`SessionId` vs `RequestId`** - two different jobs:
- `SessionId` identifies *which conversation* a message belongs to. It
  lives for the whole life of a chat, across many messages.
- `RequestId` identifies *one single prompt-and-answer round trip*. A new
  one is generated for every prompt, and it's what ties a `UserPrompt` to
  its eventual `AiResponse` (or `Error`).

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

### What is Entity Framework Core, and what are migrations?

**Entity Framework Core (EF Core)** is an ORM - "object-relational
mapper". Instead of writing SQL by hand, you write C# classes
(`ChatSession`, `ChatMessage`), and EF Core translates your C# LINQ
queries (`_db.ChatSessions.OrderByDescending(...)`) into SQL and maps the
rows back into objects. `ChatDbContext` is the central object that
represents "a session with the database".

**Migrations** solve a follow-up problem: your C# classes describe what
the tables *should* look like, but the real MySQL database needs those
tables to actually exist. A migration is a generated, version-controlled
recipe for changing the database schema to match your classes. This
project has one, `InitialCreate` (in `SocketWeb/Migrations/`), which
creates the two tables. You generate a migration with `dotnet ef
migrations add <Name>` and apply it to a real database with `dotnet ef
database update` (see README.md). Applying migrations is always a
deliberate, separate step here - the app never quietly changes the
database schema on startup.

One useful detail: the migration was generated against a *pinned* MySQL
version (`new MySqlServerVersion(...)` in `Program.cs`) rather than
`ServerVersion.AutoDetect()`, so generating and applying migrations
doesn't require a live database connection just to ask MySQL what version
it is - which is exactly why the whole DB layer could be developed and
tested on a machine with no MySQL running.

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

## The desktop bridge: one core, two UIs

The desktop side is split into a **UI-independent core** and **two thin
UIs** on top of it. This is the single most important design idea in the
project, so it's worth going slowly.

### Why split it at all?

The original desktop app was **WPF**, which is Windows-only (more on *why*
below). To also run on macOS, the interesting logic - connecting,
registering, handling `AiRequest`, reconnecting - was pulled out of the UI
into a plain .NET library, `SocketDesktop.Core`, that has **no UI
dependency at all**. A UI is then just a shell that subscribes to the
core's events and shows them. Two shells exist:

- **`SocketDesktop.Avalonia`** - cross-platform (macOS/Windows/Linux). The
  recommended client.
- **`SocketDesktop`** - the original WPF app, kept and rewired to use the
  same core (its old private copy of the logic was deleted).

Because the logic lives in one place, both UIs behave identically and there
is only one thing to test.

### `SocketDesktop.Core.DesktopSocketClient`

`DesktopSocketClient` is the desktop-side counterpart to `ChatSocketHandler`.
It owns a single managed loop: connect to `/ws`, register, pump the receive
loop, and when the connection ends, wait (with **bounded exponential
backoff**) and reconnect - until asked to stop.

```csharp
await SendAsync(new SocketMessage { Type = MessageType.ClientHello, Role = ClientRole.Desktop }, ct);
// ... receive loop ...
// on HelloAck:   State = Connected
// on AiRequest:  call IAiClient.CompleteAsync, then send AiResponse (success)
//                or Error (auth failure / timeout / provider error / malformed response)
```

It uses the same two safety tricks `SocketWeb`'s side does: a
`SemaphoreSlim` around every send (so two messages can never be written to
the socket at once), and a `ConcurrentDictionary` of already-handled
`RequestId`s (so a redelivered `AiRequest`, e.g. around a reconnect, is
never processed twice). It exposes plain C# **events** - `StateChanged`,
`ActivityLogged`, `CurrentRequestChanged` - and a `DesktopConnectionState`
enum (`Disconnected`/`Connecting`/`Connected`/`Reconnecting`). A UI just
subscribes; it doesn't know anything about WebSockets.

Two more details worth noticing:

- **Testability by abstraction.** The client doesn't `new` a real
  `ClientWebSocket`; it takes an `IClientWebSocketFactory`. Tests inject a
  *fake* socket to make `ConnectAsync` throw, feed canned messages, or
  simulate the server closing - so the whole reconnect loop is unit-tested
  deterministically, with millisecond backoffs, no real network.
- **Idempotent `Start` + graceful `StopAsync`.** `Start()` can be called
  twice (a double-clicked "Connect") without launching a second loop, and
  `StopAsync()`/`DisposeAsync()` cancel cleanly via `CancellationToken`s
  without hanging.

### Composition root instead of a full host

Neither UI uses a big generic host. Each has a tiny **composition root**
that `new`s up the object graph by hand: build `DesktopClientOptions` from
configuration (via the shared `DesktopConfiguration.Load()`), create the
`HttpClient` + `OpenAiCompatibleClient`, and construct the
`DesktopSocketClient`. Configuration is `appsettings.json` (`Ai:BaseUrl`,
`Ai:Model`, `Socket:Url` - non-secret) plus environment-variable overrides;
`AI_API_KEY` is read directly from the environment (not through the `Ai:*`
config path) so its name matches exactly what's documented and it never
ends up bound into a file. This keeps the app's lifetime entirely the UI
framework's, and makes the whole graph obvious in one small method.

### MVVM and talking back to the UI thread (Avalonia)

The Avalonia client uses **MVVM** (Model-View-ViewModel): the XAML view
(`MainWindow.axaml`) contains no logic; it *binds* to properties on a
`MainWindowViewModel` (e.g. `ConnectionStatusText`, `IsRegistered`, the
`ActivityLog` collection) and to `ICommand`s (`ConnectCommand`, ...). When
a bound property changes, the view updates automatically via
`INotifyPropertyChanged`. This is the standard way to keep UI and logic
separate and to make the view model testable on its own.

Like WPF, Avalonia only lets you touch UI state from the one UI thread, but
`DesktopSocketClient`'s events fire from a background thread (the receive
loop). So the view model marshals every handler back onto the UI thread:

```csharp
// injected as `_post`; Dispatcher.UIThread.Post in the app, a() inline in tests
_post(() => { ConnectionStatusText = ...; });
```

Injecting that "post to UI thread" delegate is what lets the same view
model run as a plain unit test (post = run inline) and as a real window
(post = `Dispatcher.UIThread.Post`). The legacy WPF window does the same
thing with `Dispatcher.Invoke(...)`.

### What the desktop window actually shows

Since the chat UI lives entirely on the web, the desktop window (either
UI) is an **operational dashboard**: WebSocket connection status,
registration status, AI configuration status (explicitly never the key
itself - only "Configured" or "Missing"), what request (if any) is being
processed right now, and a short activity log.

## Cross-cutting concepts used throughout

### Dependency injection (DI)

Rather than each class creating the objects it depends on (e.g.
`ChatSocketHandler` doing `new ChatRepository(new ChatDbContext(...))`),
those objects are provided from the outside through constructors. On the
server, `SocketWeb/Program.cs` registers them in a real DI **container**
(`builder.Services.Add...`), which decides how to build each one and how
long it lives (a **singleton** lives for the whole app; a **scoped**
service, like `ChatDbContext`, lives for one unit of work). The desktop
apps do the lighter-weight version of the same idea - a hand-written
**composition root** in `App.axaml.cs` / `App.xaml.cs` that `new`s the
graph up once. Either way the point is the same: classes stay focused on
their own job, their dependencies are explicit, and they're easy to test by
handing in a fake or in-memory version (a fake `IClientWebSocket`, a fake
`IAiClient`, the EF InMemory provider) instead of the real thing.

### async / await and CancellationToken

Almost every operation here waits on something external - a WebSocket
frame, a database query, an HTTP call to the AI. Doing that *synchronously*
would block a thread the whole time it's waiting. Instead, methods are
`async` and `await` those operations, which frees the thread to do other
work until the result is ready. That's why nearly every method returns a
`Task` and takes a `CancellationToken`.

A **`CancellationToken`** is a "stop signal" passed down through a chain of
async calls. When something should be abandoned - the AI is taking too
long, the server is shutting down, the web request was aborted - the token
is cancelled, and every awaited operation that received it stops promptly
instead of running to completion pointlessly. For example,
`DesktopSocketClient` gives each AI call a token that trips after 60
seconds, so a hung request turns into a clean timeout `Error` to the
browser rather than a browser that waits forever.

Related: **`IAsyncDisposable`/`IDisposable`**. Things that hold resources
(WebSocket connections, semaphores, the desktop's `DesktopSocketClient`
and `HttpClient`) implement a dispose method so those resources are
released deterministically - e.g. closing the desktop app calls
`DesktopSocketClient.DisposeAsync`, which stops the loop and closes its
WebSocket.

### Error handling

The guiding rule is: *one bad message must never crash the connection or
the server, and internal details must never leak to the browser.* Concretely:

- Invalid JSON is caught and answered with a clean `Error` message; the
  connection stays open and usable.
- Each AI failure mode is a distinct exception (`AiAuthenticationException`,
  `AiTimeoutException`, `AiRequestException`, `AiInvalidResponseException`),
  so the desktop can translate each into a specific, *safe* `Error` string
  - never the raw exception text (which could contain internal paths or
  connection details).
- A failed request never saves an assistant message, and never leaves the
  request stuck "pending" - so the user can simply try again.
- The browser side degrades gracefully: it shows a clear error banner,
  re-enables the composer (keeping the typed text), and - because every
  answer is persisted server-side regardless - can always recover the
  conversation by reloading it from the REST API.

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
- **The desktop core** (`SocketDesktop.Core`) is now tested *directly*,
  because it has no UI dependency. `Socket.Tests/DesktopCore` drives the
  real `DesktopSocketClient` against a fake `IClientWebSocket`: it verifies
  registration, `AiRequest` → `AiResponse` with matching ids, duplicate-id
  handling, the "AI not configured" and auth-failure error paths, reconnect
  after a dropped connection, retry after a failed connect, idempotent
  `Start`, and clean `StopAsync`/`DisposeAsync` - all with millisecond
  backoffs so it's fast and not flaky.
- **The Avalonia view model** is tested with an injected inline "post"
  delegate (so no UI thread is needed), and the **real Avalonia UI** is
  tested *headlessly* in `SocketDesktop.Avalonia.Tests` using
  `Avalonia.Headless.XUnit`: it builds the actual `MainWindow` + view model
  in a windowless Avalonia runtime and asserts the XAML bindings and
  command wiring (e.g. the Connect button disables once connected). This
  catches binding mistakes a plain unit test can't.
- **The real AI client against a real (mock) server**: one test points the
  production `OpenAiCompatibleClient` at an in-process `MockAiServer` over a
  real HTTP connection, proving the actual client↔provider path works end
  to end without a real key - the same mock the local/CI end-to-end runs use.
- **The legacy WPF `SocketDesktop` process** still can't be run off Windows
  (a `net8.0-windows`/WPF assembly can't load on macOS/Linux even with
  cross-build enabled), but that no longer matters for coverage: all of its
  logic now lives in `SocketDesktop.Core`, which *is* fully tested above.
  Only the WPF window's pixels need the Windows checklist in README.md.

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

## macOS vs Windows: what runs where

This project was developed and verified natively on macOS, and it's worth
being precise about what runs where.

**Why WPF is Windows-only, and why Avalonia isn't.** WPF renders through
Windows-specific technology (DirectX/GDI via `PresentationFramework` and
friends) that simply doesn't exist on macOS or Linux - so a WPF *runtime*
can only exist on Windows. **Avalonia** solves the same problem a different
way: it doesn't call the OS's native widgets at all. It draws every control
itself (via Skia) onto a window the OS gives it, and only needs a thin
platform-specific layer to open that window and feed it input. Because the
"draw it ourselves" part is the same everywhere, the *same* Avalonia app
runs on macOS, Windows and Linux. That's the whole reason it was added.

- **`SocketShared`, `SocketWeb`, `SocketDesktop.Core`,
  `SocketDesktop.Avalonia`, `MockAiServer`, and all tests** are fully
  cross-platform - they build **and run** on macOS, Linux, or Windows. The
  Avalonia desktop client was run as a real window on macOS and driven end
  to end against real MySQL and the mock AI.
- **`SocketDesktop`** targets `net8.0-windows` and uses WPF. With
  `<EnableWindowsTargeting>true</EnableWindowsTargeting>` it will
  **compile** ("cross-build") on macOS/Linux (useful for catching compile
  errors), but can only be *run* on Windows. Its shared logic is fully
  tested via `SocketDesktop.Core`; only its actual window needs Windows.

### The CI matrix (why two operating systems)

Because "runs on macOS" and "runs on Windows" are both claims worth
backing up automatically, CI (`.github/workflows/ci.yml`) runs the same
build/test/publish steps on **both `macos-latest` and `windows-latest`**.
The macOS runner proves the cross-platform pieces (including the Avalonia
headless UI tests) really work on macOS; the Windows runner additionally
**builds the WPF `SocketDesktop`** (the one thing macOS can only compile,
not run) and publishes the Avalonia client for Windows. Every test uses the
in-memory database and the local `MockAiServer`, so **CI never needs a real
AI key or any paid service**. This is an honest halfway point: it proves
the Windows build compiles, tests pass, and publishes - but a green Windows
CI run is *not* the same as a human clicking the WPF window, which is what
the README's Windows checklist is still for.

## How to explain this project to an internship supervisor

A 30-second version: *"It's an AI chat app. The browser talks to a web
server over a raw WebSocket - not SignalR - and the web server forwards AI
requests to a desktop app, which is the only piece that actually calls the
AI API. That indirection exists on purpose: the AI API key lives only in
the desktop app and never reaches the browser, where anyone could read it.
Chat history is stored in MySQL through Entity Framework Core, with a REST
API for managing sessions. It's fully tested - protocol, routing,
persistence, and a full browser-to-AI round trip - all without needing a
real Windows machine or a real AI account."*

If they want more depth, the things worth pointing at are:
- **The security boundary**: why the desktop app is in the loop at all
  (keeping the API key out of the browser).
- **The routed protocol**: `SessionId`/`RequestId` correlation, and how
  duplicate messages are prevented (the `RequestId` idempotency key).
- **The clean separation**: real-time AI over WebSockets vs. plain database
  CRUD over REST; and the `IAiClient` abstraction that makes the AI
  provider a config choice.
- **The engineering honesty**: exactly what was verified on macOS, what was
  verified through tests/fakes, and what still needs Windows - with a
  checklist rather than a hand-wave.
