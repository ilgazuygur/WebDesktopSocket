# WebDesktopSocket

An AI chat app with ChatGPT-style sessions, built to demonstrate an
ASP.NET Core web app and a WPF desktop app talking to each other in real
time over a **raw WebSocket connection** (no SignalR) - and, on top of
that, the desktop app acting as a secure bridge to an external AI API, so
the browser never needs (or gets) the AI provider's API key.

## Main features

- **ChatGPT-style chat sessions**: create a new chat, list previous chats
  in a sidebar, open an old chat and continue it (with full AI context),
  rename a chat, delete a chat (with confirmation), and automatic titles
  from the first message. Messages from different sessions never mix.
- **Real-time AI replies over raw WebSockets** - no SignalR, no polling.
- **The AI API key never reaches the browser**: the browser sends prompts
  to the server over WebSocket; the server routes them to the desktop app;
  only the desktop app calls the AI API (over HTTPS) with the key.
- **Provider-agnostic AI**: any OpenAI-compatible endpoint works by
  changing configuration - no code change.
- **Persistent history in MySQL** via Entity Framework Core, with a clean
  REST CRUD API (`/api/sessions`) separate from the real-time layer.
- **Robust routing and error handling**: correlated request/response IDs,
  exactly-once message saving, duplicate-message protection, desktop
  online/offline status, clear errors for offline/timeout/auth failures,
  browser auto-reconnect, and safe handling of refreshes and multiple tabs.
- **Polished, responsive web UI** in plain HTML/CSS/JS (no framework), with
  XSS-safe message rendering.

## For an internship supervisor: what this project demonstrates

- **Full-stack real-time architecture**: a browser, a web server, and a
  native desktop app coordinating over raw WebSockets, with the desktop
  app in the loop specifically so a secret (the AI API key) never has to
  reach the browser.
- **A typed, routed protocol** (`SocketMessage`) instead of a
  broadcast-to-everyone demo - the server explicitly decides who receives
  each message and why.
- **Persistence and a REST API** (EF Core + MySQL) alongside the
  real-time layer, with a clear line between "database CRUD" (HTTP) and
  "real-time AI" (WebSocket).
- **A provider-agnostic AI client abstraction** (`IAiClient`) so the exact
  AI vendor is a configuration choice, not a code change.
- **Security-conscious defaults**: no secrets committed, no API key ever
  sent to the browser, no message content ever rendered as HTML.
- **Automated tests** (95+, xUnit) covering the protocol, the AI client,
  persistence, the REST API, and the WebSocket routing/idempotency logic
  - including a fake-desktop-client integration test that exercises the
    full browser-to-AI-and-back round trip without a real Windows machine
    or a real AI API call.
- **Honesty about platform limits**: this was built primarily on macOS,
  and the README says exactly what was and wasn't verified there, with a
  precise checklist for the one part (the WPF UI) that needs Windows.

## Architecture

Three .NET 8 projects, plus a test project:

- **SocketShared** - class library with the WebSocket protocol
  (`SocketMessage`, `MessageType`, `ClientRole`, `ConversationTurn`) and
  the AI client abstraction (`IAiClient`, `OpenAiCompatibleClient`,
  `AiOptions`). Referenced by both `SocketWeb` and `SocketDesktop`, so
  both sides always agree on the exact same message shapes.
- **SocketWeb** - ASP.NET Core Razor Pages app. Hosts the chat web page,
  the raw WebSocket server (`/ws`), the session CRUD REST API
  (`/api/sessions`), and all MySQL access (via EF Core). **Never** calls
  the AI API directly.
- **SocketDesktop** - WPF desktop app. Connects to `SocketWeb` as a
  WebSocket client, registers itself as the AI bridge, and is the
  **only** project in the solution that calls the external AI API (over
  HTTPS) or holds the AI API key.
- **Socket.Tests** - xUnit test project covering everything that can run
  without Windows or a real AI/MySQL connection (see "What's tested" below).

```
                    REST  /api/sessions (CRUD)
   ┌──────────┐  ───────────────────────────────►  ┌────────────────────┐
   │ Browser  │                                     │      SocketWeb     │
   │ (JS)     │  ◄────  WS  /ws  ────────────────►  │  • Razor Pages UI  │        MySQL
   └──────────┘   ClientRole.Browser                │  • /ws server      │◄──────(EF Core,
                  (typed SocketMessage)              │  • ChatSocketHandler│       Pomelo)
                                                     │  • /api/sessions   │
                                                     └─────────┬──────────┘
                                                                │ WS  /ws
                                                       ClientRole.Desktop
                                                                │
                                                     ┌─────────▼──────────┐        AI API
                                                     │    SocketDesktop   │  HTTPS (OpenAI-
                                                     │  • DesktopSocketClient│──►  compatible)
                                                     │  • IAiClient        │
                                                     └────────────────────┘
```

## End-to-end message flow (a new chat message)

1. Browser sends `UserPrompt` over WebSocket (`SessionId`, `RequestId`,
   `Content`).
2. `SocketWeb` validates it, **saves the user message to MySQL**
   (auto-titling the session if this is its first message), loads that
   session's ordered history, and builds an `AiRequest`.
3. `SocketWeb` routes `AiRequest` to the **one** connected `SocketDesktop`
   client (never broadcasts it), and sends a `Status: thinking` message
   back to the browser.
4. `SocketDesktop`'s `DesktopSocketClient` calls `IAiClient.CompleteAsync`
   - an **HTTPS** call to the configured AI API, using the conversation
   history it was sent.
5. On success, `SocketDesktop` sends `AiResponse` back with the same
   `SessionId`/`RequestId`. On any failure (auth, timeout, provider
   error, malformed response), it sends `Error` instead - never a saved
   assistant message for a failed request.
6. `SocketWeb` receives that, **saves the assistant message to MySQL**
   (regardless of whether the original browser tab is still connected),
   and routes the result to the exact browser connection that asked -
   never to any other tab or session.
7. The browser renders the reply. If it missed the real-time push
   (refresh, dropped connection), it reloads the session via
   `GET /api/sessions/{id}` and finds the answer already there.

The **browser never talks to the AI API directly, and never receives the
API key** - it only ever talks to `SocketWeb`.

## Why raw WebSockets (not SignalR)?

A WebSocket is a long-lived, two-way connection between a client and a
server: unlike a normal HTTP request (which finishes as soon as the
response comes back), it stays open so either side can send a message at
any time. This project uses the raw `System.Net.WebSockets` API directly
on both ends rather than a library like SignalR, on purpose - it keeps
every step visible (the handshake, sending JSON, the manual receive loop,
reconnection, framing), which is exactly what makes it a good learning
project. SignalR would do all of that invisibly. See
[LEARNING_NOTES.md](LEARNING_NOTES.md) for a beginner-level walkthrough.

## Technologies used

- **.NET 8** / **C#** - all three projects target `net8.0`
  (`SocketDesktop` targets `net8.0-windows` for WPF).
- **ASP.NET Core** (Razor Pages + minimal APIs) - the web server, REST API.
- **Raw WebSockets** (`System.Net.WebSockets`) - real-time transport, no SignalR.
- **WPF** - the desktop client (`SocketDesktop`).
- **Entity Framework Core 8** with **Pomelo.EntityFrameworkCore.MySql** - persistence.
- **MySQL 8** - the database.
- **Microsoft.Extensions.Hosting / .Http** - dependency injection,
  configuration, and `IHttpClientFactory` in the desktop app.
- **Vanilla HTML / CSS / JavaScript** - the web UI (no React/Vue/paid libs).
- **xUnit** + **Microsoft.AspNetCore.Mvc.Testing** + **EF Core InMemory** - tests.

## Project folder structure

```
WebDesktopSocket/
├─ WebDesktopSocket.sln
├─ docker-compose.yml            # local MySQL for development
├─ .env.example                  # placeholder env vars (copy to .env)
├─ .config/dotnet-tools.json     # pinned local dotnet-ef tool
├─ README.md  LEARNING_NOTES.md  LICENSE
│
├─ SocketShared/                 # shared contract, referenced by both apps
│  ├─ Protocol/                  # SocketMessage, MessageType, ClientRole, ConversationTurn, SocketStatusCodes
│  └─ Ai/                        # IAiClient, OpenAiCompatibleClient, AiOptions, exceptions
│
├─ SocketWeb/                    # ASP.NET Core server (web UI + /ws + REST + MySQL)
│  ├─ Program.cs                 # DI, /ws framing loop, endpoint wiring
│  ├─ Services/                  # WebSocketConnectionManager, ChatSocketHandler, ConnectionInfo
│  ├─ Data/                      # ChatDbContext, entities, ChatRepository, SessionTitleGenerator
│  ├─ Api/                       # SessionEndpoints (/api/sessions), DTOs
│  ├─ Migrations/                # EF Core InitialCreate migration
│  ├─ Pages/                     # Index.cshtml (chat page shell)
│  └─ wwwroot/                   # css/site.css, js/{api,ws,chat}.js
│
├─ SocketDesktop/                # WPF desktop AI bridge (Windows-only runtime)
│  ├─ App.xaml(.cs)              # generic Host + DI bootstrap
│  ├─ MainWindow.xaml(.cs)       # operational dashboard
│  ├─ Services/DesktopSocketClient.cs   # the only caller of IAiClient
│  └─ appsettings.json           # non-secret Ai:BaseUrl / Ai:Model placeholders
│
└─ Socket.Tests/                 # xUnit tests (run on any OS)
   ├─ Protocol/  Ai/  Data/  Api/  Sockets/  TestInfrastructure/
```

## Session storage (MySQL) and CRUD

`SocketWeb` is the only project with database access, via EF Core
(`ChatDbContext`) and the Pomelo MySQL provider. Two tables:

- **ChatSessions**: `Id` (GUID), `Title`, `CreatedAt`, `UpdatedAt`.
- **ChatMessages**: `Id`, `SessionId` (FK, cascade delete), `Role`
  (`user`/`assistant`/`system`), `Content`, `CreatedAt`, `Sequence`
  (deterministic ordering within a session).

REST CRUD lives under `/api/sessions` (see `SocketWeb/Api/SessionEndpoints.cs`):

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/sessions` | List sessions, newest-updated first |
| `POST` | `/api/sessions` | Create a session (optional title) |
| `GET` | `/api/sessions/{id}` | Load a session with all its messages, in order |
| `GET` | `/api/sessions/{id}/messages` | Just the messages, in order |
| `PUT` | `/api/sessions/{id}` | Rename |
| `DELETE` | `/api/sessions/{id}` | Delete (cascades to its messages) |

Real-time AI requests/responses stay on the WebSocket - this REST API is
purely database CRUD.

## Requirements

- **.NET 8 SDK** - not just the runtime. Check with `dotnet --version`.
  Install from https://dotnet.microsoft.com/download/dotnet/8.0 if needed.
- **MySQL 8.0+** - via Docker Compose (recommended) or a local install.
  See "Database (MySQL)" below.
- **Windows** - only required to actually *run* `SocketDesktop` (WPF).
  Everything else (`SocketShared`, `SocketWeb`, `Socket.Tests`) runs on
  macOS/Linux too.
- An **AI API key** for `SocketDesktop` - any OpenAI-compatible provider
  (OpenAI itself, OpenRouter, a local Ollama/LM Studio server exposing an
  OpenAI-compatible endpoint, etc.).

## Required environment variables

| Variable | Used by | Purpose |
|---|---|---|
| `ConnectionStrings__ChatDb` | SocketWeb | MySQL connection string. Overrides the local-dev placeholder in `SocketWeb/appsettings.json`. |
| `AI_API_KEY` | SocketDesktop | The AI provider's API key. Read **only** from this exact environment variable - never from a committed file. |
| `MYSQL_ROOT_PASSWORD`, `MYSQL_DATABASE`, `MYSQL_USER`, `MYSQL_PASSWORD`, `MYSQL_PORT` | docker-compose.yml | Local MySQL container credentials. |

See `.env.example` for the full template with placeholder values. Copy it
to `.env` (already gitignored) and fill in your own values - never commit
the real file.

`SocketDesktop/appsettings.json` holds the **non-secret** `Ai:BaseUrl`
and `Ai:Model` values (also overridable via `Ai__BaseUrl` / `Ai__Model`
environment variables, following the same `__` convention as
`ConnectionStrings__ChatDb`).

## Database (MySQL)

### Option A: Docker Compose (recommended for local development)

```
cp .env.example .env
# edit .env and set your own local passwords
docker compose up -d
```

Then apply the schema (from the repository root):

```
dotnet tool restore
export $(grep -v '^#' .env | xargs)   # loads ConnectionStrings__ChatDb, etc. into your shell
dotnet dotnet-ef database update --project SocketWeb --startup-project SocketWeb
```

### Option B: A MySQL server you already have

1. Create a database and a user for it, e.g. in the `mysql` client:

   ```sql
   CREATE DATABASE webdesktopsocket;
   CREATE USER 'webdesktopsocket_app'@'%' IDENTIFIED BY 'your-own-password';
   GRANT ALL PRIVILEGES ON webdesktopsocket.* TO 'webdesktopsocket_app'@'%';
   ```

2. Point `SocketWeb` at it - override the local-dev placeholder in
   `appsettings.json` with an environment variable instead of editing
   that file:

   ```
   export ConnectionStrings__ChatDb="Server=127.0.0.1;Port=3306;Database=webdesktopsocket;User=webdesktopsocket_app;Password=your-own-password;"
   ```

3. Apply the schema (migration commands):

   ```
   dotnet tool restore
   dotnet dotnet-ef database update --project SocketWeb --startup-project SocketWeb
   ```

The app **never** runs migrations automatically on startup - `dotnet ef
database update` is always a deliberate, separate step. To add a new
migration after changing the entities in `SocketWeb/Data/`:

```
dotnet dotnet-ef migrations add <Name> --project SocketWeb --startup-project SocketWeb --output-dir Migrations
```

**Never commit real database credentials.** `.env` and
`appsettings.*.local.json` are gitignored; only `.env.example` (with
placeholder values) is committed.

## AI provider configuration

`SocketDesktop` is the only project that calls the AI API. Any
OpenAI-compatible provider works - set these before running it:

```
# SocketDesktop/appsettings.json (non-secret, committed with placeholders)
{
  "Ai": {
    "BaseUrl": "https://api.openai.com/v1",
    "Model": "CHANGE_ME"
  }
}
```

```
# Environment variable (Windows PowerShell shown; secret, never committed)
$env:AI_API_KEY = "sk-..."
```

If `AI_API_KEY`, `Ai:BaseUrl`, or `Ai:Model` is missing, `SocketDesktop`
still starts and connects (so you can see this clearly in its dashboard),
but any `AiRequest` it receives gets a specific
"desktop AI client is not configured" `Error` back to the browser instead
of a confusing failure.

## Startup order

MySQL must be reachable, and its schema must already be migrated, before
`SocketWeb` needs it - but `SocketWeb` itself doesn't validate the
database connection at startup (it only makes a real connection on the
first request that touches it), so the practical order is:

1. **MySQL** running with the schema applied (`dotnet ef database
   update` - see above). Do this once, or whenever migrations change.
2. **SocketWeb** (`dotnet run --project SocketWeb`) - wait for
   `Now listening on: http://localhost:5080`.
3. **SocketDesktop** (Windows only, `dotnet run --project SocketDesktop`)
   - it connects to `ws://localhost:5080/ws` as soon as it opens, so
   `SocketWeb` must already be listening.
4. **Browser** - open `http://localhost:5080`.

Steps 3 and 4 can happen in either order relative to each other; both
just need step 2 to already be running. If `SocketDesktop` isn't running
yet, the browser will simply show the AI as offline until it connects.

## Building the whole solution

From the repository root:

```
dotnet build
```

This builds `SocketShared`, `SocketWeb`, `SocketDesktop`, and
`Socket.Tests` via `WebDesktopSocket.sln`. `SocketDesktop` can be
**cross-built** (compiled, not run) on non-Windows machines - see "macOS
setup" below.

## Running tests

```
dotnet test Socket.Tests
```

`Socket.Tests` covers, without needing Windows, a real MySQL server, or a
real AI API:

- The WebSocket protocol's JSON shape (round-trips for every message type).
- The AI HTTP client, against a stubbed `HttpMessageHandler` - success,
  401/403, other non-2xx, timeout, and malformed-JSON response paths.
- The chat session/message repository, against the EF Core InMemory
  provider - CRUD, cascade delete, deterministic ordering, auto-titling.
- The `/api/sessions` REST endpoints, via `WebApplicationFactory` (a real
  in-process ASP.NET Core host) with the InMemory provider swapped in.
- The WebSocket **routing** logic (`ChatSocketHandler`,
  `WebSocketConnectionManager`) - role registration, routing an
  `AiRequest` only to the desktop, desktop-offline errors, session
  isolation, exactly-once saving, duplicate `UserPrompt`/`AiResponse`
  handling, disconnect-mid-request persistence, and safe concurrent
  sends (via a `FakeWebSocket` test double that reproduces the real
  "concurrent SendAsync" failure so the fix is actually verified).
- The **full round trip** - a real WebSocket connection acting as the
  browser and another acting as a fake desktop client, both talking to
  the real `/ws` endpoint through an in-process `TestServer` - proving
  `Browser -> SocketWeb -> Desktop -> SocketWeb -> Browser` is wired up
  and persisted correctly, without needing a real Windows machine or a
  real AI API call.

## How to run

### 1. Database

Follow "Database (MySQL)" above (Docker Compose or your own server),
then apply the schema.

### 2. SocketWeb

```
cd SocketWeb
export ConnectionStrings__ChatDb="..."   # if not already set / using .env
dotnet run
```

Wait for `Now listening on: http://localhost:5080`, then open that URL
in a browser. You'll see "Connected" (WebSocket) and "AI: Offline"
(no desktop client yet) in the header.

### 3. SocketDesktop (Windows only)

```
cd SocketDesktop
$env:AI_API_KEY = "sk-..."
dotnet run
```

Its dashboard should show "Connected" / "Registered as Desktop", and the
browser's "AI" status should flip to "Online".

### 4. Try it out

- Click **New chat**, type a message, press **Enter** (or click **Send**).
- You should see "AI is thinking...", then the assistant's reply.
- Open the sidebar to rename or delete chats, or switch between them -
  each session's history loads independently and never mixes with another.

## macOS setup (what was actually verified here)

This project was built primarily on macOS. Here's exactly what that
means for each piece:

- **SocketShared, SocketWeb, Socket.Tests**: build and run natively on
  macOS. `dotnet build` / `dotnet test` were run directly, repeatedly,
  throughout development.
- **The web UI**: verified in a real, running browser (via automated
  browser tooling) against a live `dotnet run --project SocketWeb`
  instance - layout, responsive behavior, message rendering (including
  confirming a literal `<script>` tag in a test message renders as safe
  text, not executable HTML), composer states, and error handling were
  all checked this way. Full session-CRUD-backed flows against a *real*
  MySQL database were **not** verified this way in this environment (see
  "MySQL verification" below) - session/message persistence itself is
  covered by the InMemory-backed tests instead.
- **SocketDesktop (WPF)**: **cross-builds** (compiles) on macOS via
  `<EnableWindowsTargeting>true</EnableWindowsTargeting>` in
  `SocketDesktop.csproj` - `dotnet build SocketDesktop` succeeds with 0
  warnings/errors here. This only proves the code compiles; **the WPF
  application was never run on macOS** - WPF's runtime doesn't exist
  outside Windows, full stop. Its logic (WebSocket protocol handling,
  `IAiClient` usage) is exercised indirectly: the AI client itself is
  unit-tested in `Socket.Tests`, and the exact routing/persistence
  behavior `SocketDesktop` participates in is covered by the fake-desktop
  integration tests described above. The real WPF UI and the real
  `DesktopSocketClient` process need the Windows checklist below.
- **MySQL verification**: this environment has no Docker installed, so
  the app was **not** connected to a real MySQL server here. Everything
  DB-related is verified against the EF Core InMemory provider instead
  (same repository code, same LINQ queries, same migration-independent
  schema definition - just not the real MySQL engine). If Docker becomes
  available, run `docker compose up -d`, apply the migration, and the app
  will work against it unchanged (nothing in the code assumes InMemory).

## Windows setup and verification checklist

Everything above except the WPF app itself can also just be run on
Windows normally. To verify the one part that needs it:

1. `dotnet --version` shows 8.x.
2. Start MySQL (Docker Compose or local) and confirm you can connect:
   `mysql -h 127.0.0.1 -u webdesktopsocket_app -p webdesktopsocket`.
3. `dotnet dotnet-ef database update --project SocketWeb --startup-project SocketWeb` succeeds.
4. Set `ConnectionStrings__ChatDb`, run `SocketWeb` -
   `http://localhost:5080` loads, shows "Connected" / "AI: Offline".
5. Set `AI_API_KEY` (and `Ai:BaseUrl`/`Ai:Model` if not using the
   defaults), run `SocketDesktop` - its dashboard shows "Connected" /
   "Registered as Desktop" / "API key: Configured"; the browser's "AI"
   status flips to "Online".
6. New chat -> send a prompt -> "AI is thinking" appears -> a real
   assistant reply appears. Confirm the row exists in `ChatMessages`.
7. Create a second session; confirm messages never mix between the two;
   reopen the first session and confirm its history reloads, then
   continue it and confirm the AI still has that context.
8. Rename a session (inline edit, Enter to confirm); delete a session
   (confirm dialog) and confirm its messages are gone too (cascade).
9. Stop `SocketDesktop` -> send a prompt -> browser shows a clear
   "desktop offline" error, not a hang.
10. Set an intentionally wrong `AI_API_KEY` -> send a prompt -> browser
    shows an authentication-failure error (not a raw exception).
11. Refresh the browser, and open the same chat in a second tab -> no
    duplicate messages in either; each tab is independent.
12. Close `SocketDesktop` via the window's close button and confirm the
    process exits cleanly (its `IHost`/WebSocket disposal runs without
    hanging).

## Troubleshooting

- **`SocketDesktop` dashboard shows "Disconnected" and never connects**:
  make sure `SocketWeb` is running first, and nothing else is using port
  5080.
- **Browser shows "AI: Offline"**: `SocketDesktop` isn't connected/
  registered yet, or it disconnected - the browser will flip to "Online"
  automatically once it registers (no refresh needed).
- **"The desktop AI client is offline" error when sending a prompt**: no
  `SocketDesktop` instance is currently connected. Start it, then retry -
  the same message is safe to resend.
- **"The desktop AI client is not configured" error**: `SocketDesktop` is
  connected, but `Ai:BaseUrl`, `Ai:Model`, or `AI_API_KEY` is missing -
  check its dashboard's "AI Configuration" card.
- **Authentication-failure error on every prompt**: the AI API rejected
  `AI_API_KEY` - it's wrong, expired, or doesn't match `Ai:BaseUrl`'s provider.
- **`SocketWeb` fails immediately on startup**: check
  `ConnectionStrings__ChatDb` is set (it throws a clear
  `InvalidOperationException` if missing) and that the schema has been
  migrated (`dotnet ef database update`).
- **Messages don't show up on the other side / duplicate messages**: make
  sure only ONE `SocketWeb` instance is running; refreshing the browser
  or reconnecting always reloads a session's messages from the database
  rather than appending to whatever was already on screen, specifically
  to avoid duplicates.
- **Firewall prompt on first run (Windows)**: allow it - it's just
  Windows asking permission for Kestrel (the web server) to listen on a
  local port.

## Security notes

- **The AI API key never leaves `SocketDesktop`.** It's read only from the
  `AI_API_KEY` environment variable, is never written to any committed
  file, never sent to `SocketWeb` or the browser, and is never logged or
  included in an exception message. The desktop dashboard shows only
  "Configured" / "Missing", never the value.
- **No secrets are committed.** `appsettings.json` files contain only
  `CHANGE_ME`/placeholder values; real credentials come from environment
  variables (`ConnectionStrings__ChatDb`, `AI_API_KEY`) or `.env` (which is
  gitignored). Only `.env.example` (placeholders) is committed.
- **Message content is never rendered as HTML.** The browser sets message
  text via `textContent`, never `innerHTML`, so anything a user or the AI
  sends - including something that looks like `<script>` - is shown as
  inert text. Line breaks are preserved with CSS, not by building HTML.
- **Server-generated identity.** The server assigns every connection its
  own id; a client-supplied connection id is never trusted for routing.
- **Input is validated** (empty/oversized prompts rejected, a maximum
  WebSocket message size enforced) and **internal error details are never
  forwarded to the browser** - only short, safe messages.
- **Not included** (out of scope for a learning project): user
  authentication, TLS on the local `ws://` link (the AI call itself is
  HTTPS), and rate limiting. See "Known limitations".

## Known limitations

- **WPF runs on Windows only.** `SocketDesktop` cross-builds (compiles) on
  macOS/Linux but cannot run there. See the Windows checklist.
- **No authentication** - anyone who can reach `localhost:5080` can use any
  session. Fine for a single-user local demo; a real deployment needs auth.
- **Plain `ws://`** between browser/desktop and `SocketWeb` (no `wss://`) -
  avoids dev-certificate setup. The call to the *AI API* itself is HTTPS.
- **Full conversation history is sent to the AI every request**, with no
  truncation - a very long chat could hit a provider's context limit.
- **"Most-recently-registered desktop wins"** - if two `SocketDesktop`
  instances connect, only the newest receives `AiRequest`s (no queueing).
- The in-memory `RequestId` tracking dictionaries are never trimmed - fine
  for a single long-running session, not for an unbounded-lifetime service.

## Important files

| File | What it's responsible for |
|---|---|
| `SocketShared/Protocol/SocketMessage.cs` | The one typed envelope every WebSocket message uses. |
| `SocketShared/Ai/IAiClient.cs`, `OpenAiCompatibleClient.cs` | The AI provider abstraction and its OpenAI-compatible HTTP implementation. |
| `SocketWeb/Program.cs` | Wires up DI, MySQL, the `/ws` endpoint's framing/size-limit loop, and `/api/sessions`. |
| `SocketWeb/Services/WebSocketConnectionManager.cs` | Tracks connections/roles, sends to one connection safely, notifies desktop online/offline. |
| `SocketWeb/Services/ChatSocketHandler.cs` | The actual routing/business logic: validate, save, load history, route `AiRequest`/`AiResponse`/`Error`, idempotency. |
| `SocketWeb/Data/ChatRepository.cs` | All MySQL reads/writes - sessions, messages, auto-titling, ordering. |
| `SocketWeb/Api/SessionEndpoints.cs` | The `/api/sessions` REST CRUD endpoints. |
| `SocketWeb/wwwroot/js/{api,ws,chat}.js` | The browser: REST calls, WebSocket connection/reconnect, and all UI rendering/state. |
| `SocketDesktop/Services/DesktopSocketClient.cs` | Connects to `SocketWeb`, handles `AiRequest`, calls `IAiClient`, replies. The only place that calls the AI API. |
| `SocketDesktop/App.xaml.cs` | Bootstraps DI/configuration (`Ai:*`, `AI_API_KEY`) for the desktop app. |

See [LEARNING_NOTES.md](LEARNING_NOTES.md) for a deeper explanation of
*how* the code works, aimed at someone learning WebSockets/ASP.NET
Core/WPF/EF Core for the first time.

## License

MIT - see [LICENSE](LICENSE).
