# WebDesktopSocket

An AI chat app with ChatGPT-style sessions, built to demonstrate an
ASP.NET Core web app and a native desktop app talking to each other in
real time over a **raw WebSocket connection** (no SignalR) - and, on top
of that, the desktop app acting as a secure bridge to an external AI API,
so the browser never needs (or gets) the AI provider's API key.

The desktop bridge ships in two forms sharing one UI-independent core
(`SocketDesktop.Core`):

- **`SocketDesktop.Avalonia`** - the cross-platform client. Runs natively
  on **macOS and Windows** (and Linux). This is the recommended client and
  the one used for the local end-to-end verification below.
- **`SocketDesktop`** - the original **Windows-only** WPF client, kept and
  adapted to reuse the same shared core, so nothing was lost in the move.

> **Verified locally on macOS (Apple Silicon):** the whole stack was run
> natively end to end - real MySQL (Docker), `SocketWeb`, the deterministic
> local mock AI server, and `SocketDesktop.Avalonia` as a real window -
> and driven through a real browser, with every message checked for
> exactly-once persistence in MySQL. See
> [macOS setup](#macos-setup-what-was-actually-verified-here). The Windows
> WPF build is verified by CI (compile + publish) and Windows CI runs of
> the Avalonia client, but its GUI was not physically clicked on a Windows
> machine - the README is explicit about that distinction throughout.

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
- **Automated tests** (127, xUnit) covering the protocol, the AI client,
  persistence, the REST API, the WebSocket routing/idempotency logic, the
  UI-independent desktop core (connection lifecycle, reconnect,
  cancellation, shutdown, AI routing), the Avalonia view model, and
  **Avalonia headless UI tests** - plus a fake-desktop integration test
  and a real `OpenAiCompatibleClient`↔`MockAiServer` test that exercise the
  full round trip without a real Windows machine or a real AI API call.
- **A shared, UI-independent core** (`SocketDesktop.Core`) with two UIs on
  top of it (cross-platform Avalonia + legacy WPF), showing separation of
  concerns and cross-platform .NET.
- **Honesty about platform limits**: this was built and verified natively
  on macOS, and the README says exactly what was and wasn't verified there
  vs. only on the Windows CI runner.

## Architecture

.NET 8 projects:

- **SocketShared** - class library with the WebSocket protocol
  (`SocketMessage`, `MessageType`, `ClientRole`, `ConversationTurn`) and
  the AI client abstraction (`IAiClient`, `OpenAiCompatibleClient`,
  `AiOptions`). Referenced by `SocketWeb` and by the desktop core, so all
  sides always agree on the exact same message shapes.
- **SocketWeb** - ASP.NET Core Razor Pages app. Hosts the chat web page,
  the raw WebSocket server (`/ws`), the session CRUD REST API
  (`/api/sessions`), and all MySQL access (via EF Core). **Never** calls
  the AI API directly.
- **SocketDesktop.Core** - UI-independent class library with all the
  desktop bridge logic: WebSocket connection lifecycle, desktop
  registration, `AiRequest` handling (the **only** caller of `IAiClient`),
  bounded-exponential-backoff reconnect, cancellation/shutdown, and
  configuration loading/validation. **No WPF or Avalonia dependency** -
  it's pure .NET, so both desktop UIs use it unchanged and it's fully
  unit-testable against a fake WebSocket.
- **SocketDesktop.Avalonia** - cross-platform desktop app (macOS, Windows,
  Linux). A thin MVVM shell over `SocketDesktop.Core`: an operational
  dashboard (connection/registration/AI-config status, activity log,
  connect/disconnect/reconnect). **This is the recommended client.**
- **SocketDesktop** - the original **Windows-only** WPF app, adapted to
  reuse `SocketDesktop.Core` instead of its own copy of the logic. Kept so
  the move to cross-platform lost nothing.
- **MockAiServer** - a small deterministic, OpenAI-compatible HTTP server
  used only for testing/local runs. It returns a fixed, recognizable reply
  and ignores the API key, so end-to-end runs need no real provider or key.
- **Socket.Tests** - xUnit tests covering the protocol, AI client,
  persistence, REST API, WebSocket routing, `SocketDesktop.Core`, the
  Avalonia view model, and a real `OpenAiCompatibleClient`↔`MockAiServer`
  integration test.
- **SocketDesktop.Avalonia.Tests** - Avalonia **headless** UI tests that
  build the real `MainWindow`/view model and assert bindings and command
  wiring, without a display.

Whichever desktop client runs, the flow is identical:

```
                    REST  /api/sessions (CRUD)
   ┌──────────┐  ───────────────────────────────►  ┌─────────────────────┐
   │ Browser  │                                     │      SocketWeb      │
   │ (JS)     │  ◄────  WS  /ws  ────────────────►  │  • Razor Pages UI   │       MySQL
   └──────────┘   ClientRole.Browser                │  • /ws server       │◄─────(EF Core,
                  (typed SocketMessage)              │  • ChatSocketHandler│      Pomelo)
                                                     │  • /api/sessions    │
                                                     └─────────┬───────────┘
                                                               │ WS  /ws
                                                      ClientRole.Desktop
                                                               │
                                        ┌──────────────────────▼───────────────────┐   AI API
                                        │  SocketDesktop.Avalonia  (macOS/Windows)  │  HTTPS
                                        │      or  SocketDesktop   (Windows/WPF)     │  (OpenAI-
                                        │  ── both are thin shells over ──           │──► compat.)
                                        │        SocketDesktop.Core                   │
                                        │   • DesktopSocketClient  • IAiClient        │
                                        └────────────────────────────────────────────┘
```

The **browser never talks to the AI provider directly**, and the AI call
never happens inside `SocketWeb` - it is always made from the desktop
process, which is the only place the API key lives.

## The desktop app: why Avalonia, and how the code is split

**Why Avalonia was added.** The original desktop client was WPF, which
only runs on Windows. To make the AI bridge usable on the machine this was
actually developed on (macOS) - without throwing the WPF app away - the
desktop logic was extracted into a plain .NET library and given a second,
cross-platform UI built with [Avalonia](https://avaloniaui.net/). Avalonia
is a XAML/MVVM UI framework whose runtime works on macOS, Windows and
Linux, so the same desktop bridge now runs natively wherever .NET does.

**`SocketDesktop.Core` responsibilities (no UI dependency):**

- Opening and owning the WebSocket connection to `SocketWeb`, and
  registering as `ClientRole.Desktop`.
- Handling each `AiRequest` by calling `IAiClient` (the single place in the
  whole solution that does), and replying with `AiResponse` or a specific,
  safe `Error`.
- A single managed connect/receive/reconnect loop with **bounded
  exponential backoff**, graceful `StopAsync`, `CancellationToken`
  propagation, idempotent `Start`, and duplicate-`RequestId` protection.
- Loading and validating configuration (`Socket__Url`, `Ai__BaseUrl`,
  `Ai__Model`, `AI_API_KEY`) and describing what's missing - never
  exposing the key value.
- Raising plain events (`StateChanged`, `ActivityLogged`,
  `CurrentRequestChanged`) that any UI can subscribe to.

**`SocketDesktop.Avalonia` responsibilities:** just the UI. A small MVVM
composition root wires up `DesktopSocketClient` and binds its events to an
observable view model; the window shows connection, registration and AI
configuration status (never the key), the current request, and a live
activity log, with Connect/Disconnect/Reconnect buttons. All events are
marshalled to the UI thread; network calls stay off it.

**Legacy `SocketDesktop` (WPF) status:** still builds and is still
supported on Windows, now delegating all of its networking/AI logic to
`SocketDesktop.Core` (its own old `Services/DesktopSocketClient.cs` was
removed). It cross-**builds** on macOS but, being WPF, can only **run** on
Windows. New work should target the Avalonia client.

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

- **.NET 8** / **C#** - projects target `net8.0` (`SocketDesktop` targets
  `net8.0-windows` for WPF; `SocketDesktop.Avalonia` is plain `net8.0`).
- **ASP.NET Core** (Razor Pages + minimal APIs) - the web server, REST API.
- **Raw WebSockets** (`System.Net.WebSockets`) - real-time transport, no SignalR.
- **Avalonia 11 (Fluent theme)** - the cross-platform desktop client
  (`SocketDesktop.Avalonia`), with **Avalonia.Headless.XUnit** for its
  windowless UI tests.
- **WPF** - the legacy Windows-only desktop client (`SocketDesktop`).
- **Entity Framework Core 8** with **Pomelo.EntityFrameworkCore.MySql** - persistence.
- **MySQL 8** - the database.
- **Microsoft.Extensions.Configuration** - non-secret defaults +
  environment-variable overrides, shared by both desktop clients via
  `SocketDesktop.Core` (each app composes its object graph by hand rather
  than pulling in a full generic host).
- **Vanilla HTML / CSS / JavaScript** - the web UI (no React/Vue/paid libs).
- **xUnit** + **Microsoft.AspNetCore.Mvc.Testing** + **EF Core InMemory**
  + **Avalonia.Headless** - tests.
- **GitHub Actions** - CI on `macos-latest` and `windows-latest`
  (build, test, headless UI tests, and cross-platform publish).

## Project folder structure

```
WebDesktopSocket/
├─ WebDesktopSocket.sln
├─ docker-compose.yml            # local MySQL for development
├─ .env.example                  # placeholder env vars (copy to .env)
├─ .config/dotnet-tools.json     # pinned local dotnet-ef tool
├─ .github/workflows/ci.yml      # macOS + Windows CI (build/test/publish)
├─ scripts/                      # run-local-macos.sh, test-all.sh, publish-*.{sh,ps1}
├─ README.md  LEARNING_NOTES.md  LICENSE
│
├─ SocketShared/                 # shared contract, referenced by web + desktop core
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
├─ SocketDesktop.Core/           # UI-independent bridge logic (no WPF/Avalonia)
│  ├─ DesktopSocketClient.cs     # connect/reconnect loop, the only caller of IAiClient
│  ├─ DesktopClientOptions.cs    # config + validation; DesktopConfiguration loader
│  ├─ DesktopConnectionState.cs  # Disconnected/Connecting/Connected/Reconnecting
│  └─ Sockets/                   # IClientWebSocket abstraction + real impl (for testability)
│
├─ SocketDesktop.Avalonia/       # cross-platform desktop AI bridge (macOS/Windows/Linux)
│  ├─ App.axaml(.cs)             # manual composition root (DI by hand)
│  ├─ MainWindow.axaml(.cs)      # dashboard XAML
│  ├─ ViewModels/                # MainWindowViewModel, RelayCommand, ViewModelBase (MVVM)
│  └─ appsettings.json           # non-secret Ai:BaseUrl / Ai:Model / Socket:Url placeholders
│
├─ SocketDesktop/                # legacy WPF desktop AI bridge (Windows-only runtime)
│  ├─ App.xaml(.cs)              # manual composition root over SocketDesktop.Core
│  ├─ MainWindow.xaml(.cs)       # operational dashboard
│  └─ appsettings.json           # non-secret Ai:BaseUrl / Ai:Model placeholders
│
├─ MockAiServer/                 # deterministic OpenAI-compatible test server
│  ├─ MockOpenAiServer.cs        # fixed reply, ignores API key; in-proc or standalone
│  └─ Program.cs                 # standalone entrypoint (default port 5099)
│
├─ Socket.Tests/                 # xUnit tests (run on any OS)
│  ├─ Protocol/  Ai/  Data/  Api/  Sockets/  TestInfrastructure/
│  └─ DesktopCore/               # SocketDesktop.Core + Avalonia VM + MockAiServer tests
│
└─ SocketDesktop.Avalonia.Tests/ # Avalonia headless UI tests
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
- **macOS, Windows, or Linux** to run the recommended desktop client
  (`SocketDesktop.Avalonia`). **Windows** is required only to *run* the
  legacy `SocketDesktop` (WPF) - everything else runs on any of the three.

### macOS prerequisites

- .NET 8 SDK (`dotnet --version` → `8.x`).
- Docker Desktop (for the MySQL container).
- No extra GUI dependencies - Avalonia runs on stock macOS.

### Windows prerequisites

- .NET 8 SDK.
- Docker Desktop, or a local/remote MySQL 8 server.
- The **Desktop development / .NET desktop** workload is only needed to
  build the WPF `SocketDesktop` project; the Avalonia client needs nothing
  beyond the SDK.

For real AI replies you also need an **AI API key** for any
OpenAI-compatible provider (OpenAI, OpenRouter, a local Ollama/LM Studio
"OpenAI compatible" endpoint, etc.). For local testing you don't need one -
use the bundled [MockAiServer](#local-deterministic-mock-ai-mockaiserver).

## Required environment variables

| Variable | Used by | Purpose |
|---|---|---|
| `ConnectionStrings__ChatDb` | SocketWeb | MySQL connection string. Overrides the local-dev placeholder in `SocketWeb/appsettings.json`. |
| `Socket__Url` | desktop client | WebSocket URL of `SocketWeb` (default `ws://localhost:5080/ws`). Rarely needs changing locally. |
| `Ai__BaseUrl` | desktop client | Base URL of the OpenAI-compatible AI endpoint, e.g. `https://api.openai.com/v1` or `http://localhost:5099/v1` for the mock. Non-secret. |
| `Ai__Model` | desktop client | Model name, e.g. `gpt-4o-mini` or `mock-model`. Non-secret. |
| `AI_API_KEY` | desktop client | The AI provider's API key. Read **only** from this exact environment variable - never from a committed file. For the mock, any non-empty value works. |
| `MYSQL_ROOT_PASSWORD`, `MYSQL_DATABASE`, `MYSQL_USER`, `MYSQL_PASSWORD`, `MYSQL_PORT` | docker-compose.yml | Local MySQL container credentials. |

The `__` (double underscore) convention maps an environment variable to a
nested configuration key (`Ai__BaseUrl` → `Ai:BaseUrl`), the standard
.NET configuration mechanism. `AI_API_KEY` is deliberately a flat name,
read directly, so it matches exactly what's documented and is never bound
through a config section that could end up in a file.

Both desktop clients read the same variables via `SocketDesktop.Core`, and
each app's `appsettings.json` holds only the **non-secret** `Ai:BaseUrl` /
`Ai:Model` (and `Socket:Url`) placeholders.

See `.env.example` for the full template with placeholder values. Copy it
to `.env` (already gitignored) and fill in your own values - never commit
the real file.

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
2. *(optional, for local testing)* **MockAiServer**
   (`dotnet run --project MockAiServer`) if you don't want to use a real
   AI provider - see below.
3. **SocketWeb** (`dotnet run --project SocketWeb`) - wait for
   `Now listening on: http://localhost:5080`.
4. **Desktop client** - the cross-platform
   `dotnet run --project SocketDesktop.Avalonia` (macOS/Windows/Linux), or
   the Windows-only `dotnet run --project SocketDesktop` (WPF). It connects
   to `ws://localhost:5080/ws` as soon as it opens, so `SocketWeb` must
   already be listening.
5. **Browser** - open `http://localhost:5080`.

Steps 4 and 5 can happen in either order relative to each other; both
just need `SocketWeb` to already be running. If no desktop client is
running yet, the browser simply shows the AI as offline until one
connects (and flips to online automatically when it does).

## Building the whole solution

From the repository root:

```
dotnet build
```

This builds every project via `WebDesktopSocket.sln`. The legacy WPF
`SocketDesktop` can be **cross-built** (compiled, not run) on non-Windows
machines thanks to `<EnableWindowsTargeting>true</EnableWindowsTargeting>`
- see "macOS setup" below. The Avalonia client (`SocketDesktop.Avalonia`)
builds *and runs* on macOS, Windows and Linux.

## Running tests

```
dotnet test              # whole solution: Socket.Tests + SocketDesktop.Avalonia.Tests
```

or the convenience wrapper [`scripts/test-all.sh`](scripts/test-all.sh).
The suite (127 tests) runs without needing Windows, a real MySQL server,
or a real AI API. `SocketDesktop.Avalonia.Tests` holds the Avalonia
headless UI tests; `Socket.Tests` covers everything else, including the
`SocketDesktop.Core` connection/reconnect/routing logic and a real
`OpenAiCompatibleClient`↔`MockAiServer` integration test. Specifically:

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

### 3. Desktop client

**macOS / Linux / Windows - Avalonia (recommended):**

```bash
# macOS/Linux (bash/zsh)
export Ai__BaseUrl="https://api.openai.com/v1"   # or http://localhost:5099/v1 for the mock
export Ai__Model="gpt-4o-mini"                    # or mock-model for the mock
export AI_API_KEY="sk-..."                        # any non-empty value for the mock
dotnet run --project SocketDesktop.Avalonia
```

```powershell
# Windows (PowerShell) - Avalonia
$env:Ai__BaseUrl = "https://api.openai.com/v1"
$env:Ai__Model   = "gpt-4o-mini"
$env:AI_API_KEY  = "sk-..."
dotnet run --project SocketDesktop.Avalonia
```

**Windows only - legacy WPF:**

```powershell
$env:AI_API_KEY = "sk-..."
dotnet run --project SocketDesktop
```

Either client's dashboard should show "Connected" / "Registered as
Desktop", and the browser's "AI" status should flip to "Online".

### 4. Try it out

- Click **New chat**, type a message, press **Enter** (or click **Send**).
- You should see "AI is thinking...", then the assistant's reply.
- Open the sidebar to rename or delete chats, or switch between them -
  each session's history loads independently and never mixes with another.

## Local deterministic mock AI (MockAiServer)

`MockAiServer` is a tiny OpenAI-compatible HTTP server that returns a
fixed, recognizable reply and ignores the `Authorization` header. It lets
you (and CI) run the whole stack end to end **without a real AI provider
or key**. Point the desktop client's `Ai__BaseUrl` at it:

```bash
# terminal 1 - the mock (defaults to port 5099; override with the first arg
# or MOCK_PORT, and the reply text with MOCK_REPLY)
dotnet run --project MockAiServer

# terminal 2 - the desktop client, pointed at the mock
export Ai__BaseUrl="http://localhost:5099/v1"
export Ai__Model="mock-model"
export AI_API_KEY="local-dummy-key-not-secret"   # ignored by the mock
dotnet run --project SocketDesktop.Avalonia
```

Now every prompt in the browser comes back as the mock's deterministic
reply, routed through the exact same `Browser → SocketWeb → Desktop →
AI → Desktop → SocketWeb → Browser` path a real provider would use. This
is precisely how the local macOS end-to-end run and the CI-safe tests
avoid needing a real key. The helper script
[`scripts/run-local-macos.sh`](scripts/run-local-macos.sh) starts the
mock, `SocketWeb`, and the Avalonia client together.

## Building and publishing

Build everything (see also "Building the whole solution" above):

```bash
dotnet build -c Release
```

Publish the cross-platform Avalonia client as a self-contained app for a
specific runtime:

```bash
# macOS Apple Silicon
dotnet publish SocketDesktop.Avalonia -c Release -r osx-arm64 --self-contained
# macOS Intel
dotnet publish SocketDesktop.Avalonia -c Release -r osx-x64  --self-contained
# Windows x64
dotnet publish SocketDesktop.Avalonia -c Release -r win-x64  --self-contained
```

Helper scripts wrap these: [`scripts/publish-macos.sh`](scripts/publish-macos.sh)
(osx-arm64 + osx-x64) and [`scripts/publish-windows.ps1`](scripts/publish-windows.ps1)
(win-x64). Published output goes under each project's `bin/.../publish/`
and is gitignored. The legacy WPF `SocketDesktop` is published on Windows
only.

> **macOS Gatekeeper / Windows SmartScreen:** these published builds are
> **unsigned**. On macOS, Gatekeeper may refuse to open them ("cannot be
> opened because the developer cannot be verified") - right-click → Open,
> or clear the quarantine attribute (`xattr -dr com.apple.quarantine
> <app>`). On Windows, SmartScreen may warn ("Windows protected your PC") -
> choose "More info" → "Run anyway". Code signing is out of scope for this
> project. During development you just use `dotnet run`, which is unaffected.

## Continuous integration (GitHub Actions)

`.github/workflows/ci.yml` runs on every push/PR on a matrix of
**`macos-latest`** and **`windows-latest`**:

1. Checks out the repo and installs the **.NET 8 SDK**.
2. `dotnet restore` + `dotnet build -c Release`.
3. Runs the full test suite, including the **Avalonia headless UI tests**.
   All tests use the in-memory database and the local `MockAiServer`, so
   **no real AI key or paid service is ever required**.
4. Publishes `SocketDesktop.Avalonia` for `osx-arm64` + `osx-x64` (on the
   macOS runner) and `win-x64` (on the Windows runner), and **builds the
   legacy WPF `SocketDesktop` on Windows**.
5. Uploads the publish outputs as build artifacts.

The job fails on any real build, test, or publish failure. WPF is only
ever built (never run) on the Windows runner; it is never touched on macOS.

## macOS setup (what was actually verified here)

This project was built and verified end to end on macOS (Apple Silicon).
Here's exactly what that means for each piece:

- **SocketShared, SocketWeb, SocketDesktop.Core, SocketDesktop.Avalonia,
  MockAiServer, tests**: build and run natively on macOS. `dotnet build` /
  `dotnet test` were run directly, repeatedly, throughout development
  (whole solution: 0 warnings, 0 errors; 127 tests passing).
- **Real MySQL**: verified against a **real MySQL 8 server** running via
  `docker compose up -d` (not just the InMemory provider). EF Core
  migrations were applied against it, and the `ChatSessions`,
  `ChatMessages` and `__EFMigrationsHistory` tables were confirmed.
- **`SocketDesktop.Avalonia`**: **run natively as a real window on macOS**,
  connected to `SocketWeb`, registered as the Desktop client, and reported
  live connection/registration/AI status. It stayed responsive, performed
  the AI call off the UI thread, and shut down cleanly.
- **The web UI + full round trip**: driven in a **real browser** against
  the live stack (SocketWeb + Avalonia desktop + `MockAiServer`). Verified:
  the page loads, WebSocket connects, AI shows online, a chat can be
  created, a prompt gets the deterministic mock reply in the correct
  session, refresh keeps the session/messages, rename works, a second
  session's messages stay separate, delete persists across refresh,
  closing the desktop app flips the browser to "AI: Offline" and sending
  then yields a clean error (no crash), restarting the desktop returns it
  to online, and restarting `SocketWeb` preserves sessions in MySQL while
  the desktop auto-reconnects and a follow-up prompt still completes with
  **no duplicate response**.
- **Exactly-once persistence**: after each tested prompt, MySQL was queried
  directly to confirm exactly one `user` row and one `assistant` row per
  request, correct roles/ordering, and **zero duplicate message groups**.
- **Legacy `SocketDesktop` (WPF)**: **cross-builds** (compiles) on macOS
  via `<EnableWindowsTargeting>true</EnableWindowsTargeting>` -
  `dotnet build` succeeds with 0 warnings/errors here. This only proves it
  compiles; **the WPF application was never run on macOS** - WPF's runtime
  doesn't exist outside Windows. Its shared logic is exercised through
  `SocketDesktop.Core`'s unit tests and the routing/integration tests. The
  real WPF UI needs the Windows checklist below (and CI builds it on the
  Windows runner).

## Windows setup and verification checklist

On Windows you can run either desktop client. `SocketDesktop.Avalonia` is
covered by CI on `windows-latest` (build + headless UI tests + publish),
but its Windows GUI and the WPF `SocketDesktop` GUI were **not physically
clicked on a Windows machine** during this work - that is what this
checklist is for. Substitute `dotnet run --project SocketDesktop.Avalonia`
for step 5 to exercise the cross-platform client instead of WPF. To verify:

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
12. Close the desktop app via the window's close button and confirm the
    process exits cleanly (its `DesktopSocketClient.DisposeAsync` -
    stopping the loop and closing the WebSocket - runs without hanging).

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

- **Legacy WPF runs on Windows only.** `SocketDesktop` cross-builds
  (compiles) on macOS/Linux but cannot run there - use
  `SocketDesktop.Avalonia` for cross-platform. See the Windows checklist.
- **Unsigned desktop builds.** Published Avalonia/WPF binaries are not code
  signed, so macOS Gatekeeper / Windows SmartScreen will warn on first
  launch (see "Building and publishing"). Fine for a demo, not for
  distribution.
- **Windows GUI not physically inspected.** The Windows builds are verified
  by CI (compile/test/publish) only; no one clicked the WPF or Windows
  Avalonia window on a real Windows desktop during this work.
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
| `SocketDesktop.Core/DesktopSocketClient.cs` | Connects to `SocketWeb`, handles `AiRequest`, calls `IAiClient`, reconnect loop. The only place that calls the AI API. Shared by both desktop UIs. |
| `SocketDesktop.Core/DesktopClientOptions.cs`, `DesktopConfiguration.cs` | Desktop configuration and its validation (`Socket__Url`, `Ai__*`, `AI_API_KEY`). |
| `SocketDesktop.Avalonia/ViewModels/MainWindowViewModel.cs` | The Avalonia dashboard view model (MVVM) binding `DesktopSocketClient` events to the UI. |
| `SocketDesktop.Avalonia/App.axaml.cs` | Composition root: wires up `DesktopSocketClient`, the AI client, and the window. |
| `SocketDesktop/App.xaml.cs`, `MainWindow.xaml.cs` | The legacy WPF app, now delegating to `SocketDesktop.Core`. |
| `MockAiServer/MockOpenAiServer.cs` | Deterministic OpenAI-compatible test server (fixed reply, ignores the key). |

See [LEARNING_NOTES.md](LEARNING_NOTES.md) for a deeper explanation of
*how* the code works, aimed at someone learning WebSockets/ASP.NET
Core/Avalonia/WPF/EF Core for the first time.

## License

MIT - see [LICENSE](LICENSE).
