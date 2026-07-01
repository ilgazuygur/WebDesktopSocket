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
`WebDesktopSocket.sln`.

## Fixed configuration

- Web server URL: `http://localhost:5080`
- WebSocket endpoint: `ws://localhost:5080/ws`

Both are hardcoded (in `SocketWeb/Program.cs`, `SocketWeb/wwwroot/js/socket-client.js`,
and `SocketDesktop/Services/SocketClientService.cs`) to keep the demo simple -
no configuration files to edit before it works.

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
