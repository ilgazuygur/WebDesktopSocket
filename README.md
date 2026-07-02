# WebDesktopSocket

A minimal demo showing an ASP.NET Core web page and a WPF desktop app
talking to each other in real time over a raw WebSocket connection
(no SignalR), using plain JSON messages.

## Projects

- **SocketShared** - class library with the shared `ChatMessage` model.
- **SocketWeb** - ASP.NET Core Razor Pages app. Hosts the web page AND the
  WebSocket server (`/ws` endpoint).
- **SocketDesktop** - WPF desktop app. Connects to `SocketWeb` as a
  WebSocket client using `ClientWebSocket`.

## Requirements

- Windows (required for the WPF desktop app).
- **.NET 8 SDK** - not just the runtime. Check with:

  ```
  dotnet --version
  ```

  If that fails, install the .NET 8 SDK from:
  https://dotnet.microsoft.com/download/dotnet/8.0

## How to run

You need **two things running at once**: the web server, and the desktop
app. Use two separate terminals.

### 1. Start the web server (SocketWeb)

```
cd SocketWeb
dotnet run
```

Wait until you see a line like `Now listening on: http://localhost:5080`.

Leave this terminal running - if you stop it, both the web page and the
desktop app will disconnect.

Open your browser to:

```
http://localhost:5080
```

The status on the page should switch to **Connected**.

### 2. Start the desktop app (SocketDesktop)

In a **second** terminal:

```
cd SocketDesktop
dotnet run
```

A window titled "SocketDesktop - WebSocket Chat Demo" should appear, and
its status should switch to **Connected** too.

> Important: start `SocketWeb` first. `SocketDesktop` tries to connect to
> `ws://localhost:5080/ws` as soon as it opens, so the server needs to
> already be listening.

### 3. Try it out

- Type a message in the browser page and click **Send** (or press Enter).
  It should instantly appear in both the web page's log AND the desktop
  app's log.
- Type a message in the desktop app and click **Send** (or press Enter).
  It should instantly appear in both logs as well.
- You can also open a second browser tab to `http://localhost:5080` - it
  will join the same conversation too.

## Building the whole solution

From the repository root:

```
dotnet build
```

This builds `SocketShared`, `SocketWeb`, and `SocketDesktop` together via
`WebDesktopSocket.sln`. (On macOS, `SocketDesktop` cannot build - see
"macOS limitations" below.)

## Running tests

```
dotnet test Socket.Tests
```

`Socket.Tests` covers the WebSocket protocol JSON, the AI HTTP client
(against a stubbed handler, no real network/API calls), and the chat
session/message repository (against the EF Core InMemory provider, no
real MySQL needed). These all run on any OS, including macOS.

## Database (MySQL)

`SocketWeb` persists chat sessions and messages to MySQL via EF Core
(Pomelo provider). Nothing else (not the browser, not `SocketDesktop`)
talks to the database directly.

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

2. Point `SocketWeb` at it. `appsettings.json` only contains a local-dev
   placeholder connection string - override it with an environment
   variable instead of editing that file:

   ```
   export ConnectionStrings__ChatDb="Server=127.0.0.1;Port=3306;Database=webdesktopsocket;User=webdesktopsocket_app;Password=your-own-password;"
   ```

3. Apply the schema:

   ```
   dotnet tool restore
   dotnet dotnet-ef database update --project SocketWeb --startup-project SocketWeb
   ```

The app never runs migrations automatically on startup - `dotnet ef
database update` (or a CI/deploy step that does the same) is always a
deliberate, separate step.

**Never commit real database credentials.** `.env` and
`appsettings.*.local.json` are gitignored; only `.env.example` (with
placeholder values) is committed. See `.env.example` for the full list of
variables.

## Fixed configuration

- Web server URL: `http://localhost:5080`
- WebSocket endpoint: `ws://localhost:5080/ws`

Both are hardcoded (in `SocketWeb/Program.cs`, `SocketWeb/wwwroot/js/socket-client.js`,
and `SocketDesktop/Services/SocketClientService.cs`) to keep the demo simple -
no configuration files to edit before it works.

The MySQL connection string is the one thing that **is** configurable -
see "Database (MySQL)" above.

## Troubleshooting

- **Desktop app shows "Disconnected" and never connects**: make sure
  `SocketWeb` is running first, and that nothing else on your machine is
  already using port 5080.
- **Messages don't show up on the other side**: make sure you only have
  ONE instance of `SocketWeb` running. If you accidentally start it twice,
  the web page and desktop app might end up talking to different
  instances.
- **Firewall prompt on first run**: allow it - it's just Windows asking
  permission for Kestrel (the web server) to listen on a local port.

See [LEARNING_NOTES.md](LEARNING_NOTES.md) for an explanation of how the
code works.

## License

MIT - see [LICENSE](LICENSE).
