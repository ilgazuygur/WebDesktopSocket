using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SocketShared;
using SocketWeb.Services;

var builder = WebApplication.CreateBuilder(args);

// Make sure the site always runs on the same, fixed port, so the WPF
// desktop app always knows exactly where to connect.
builder.WebHost.UseUrls("http://localhost:5080");

builder.Services.AddRazorPages();

// The connection manager is a singleton: there is exactly one instance
// for the whole application, shared by every request, so it can keep
// track of every open WebSocket connection in one place.
builder.Services.AddSingleton<WebSocketConnectionManager>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

// This turns on ASP.NET Core's support for handling WebSocket upgrade
// requests. Without this line, the /ws endpoint below would not work.
app.UseWebSockets();

// This is the raw WebSocket endpoint. Both the browser's JavaScript
// WebSocket and the desktop app's ClientWebSocket connect here.
app.Map("/ws", async (HttpContext context, WebSocketConnectionManager manager) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var socketId = manager.AddSocket(socket);

    var buffer = new byte[1024 * 4];

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                break;
            }

            // Read the JSON text that was sent to us and parse it into the
            // shared ChatMessage model, so both the web page and the
            // desktop app are guaranteed to be talking about the same shape.
            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var chatMessage = JsonSerializer.Deserialize<ChatMessage>(json);

            if (chatMessage is not null)
            {
                // Re-serialize and immediately forward it to every connected
                // client (the browser page and the desktop app), so they all
                // see the new message, including the sender itself.
                var outgoingJson = JsonSerializer.Serialize(chatMessage);
                await manager.BroadcastAsync(outgoingJson);
            }
        }
    }
    catch (WebSocketException)
    {
        // Happens when a client disconnects abruptly (e.g. app closed).
        // Nothing special to do — we just clean up below.
    }
    finally
    {
        manager.RemoveSocket(socketId);
    }
});

app.MapRazorPages();

app.Run();
