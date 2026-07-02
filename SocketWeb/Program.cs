using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SocketShared;
using SocketWeb.Data;
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

// SocketWeb is the only project that talks to MySQL - the connection
// string comes from configuration (appsettings.json holds only a local
// placeholder; the real value is supplied via the ConnectionStrings__ChatDb
// environment variable or user-secrets, never committed - see
// .env.example and README.md).
var chatDbConnectionString = builder.Configuration.GetConnectionString("ChatDb")
    ?? throw new InvalidOperationException("Missing configuration: ConnectionStrings:ChatDb");

// Pinned to a specific MySQL version instead of ServerVersion.AutoDetect(),
// so starting the app (and running `dotnet ef migrations add`) never needs
// to open a real database connection just to ask MySQL which version it
// is. Update this if you deploy against a different MySQL major/minor
// version.
var mySqlServerVersion = new MySqlServerVersion(new Version(8, 0, 36));

builder.Services.AddDbContext<ChatDbContext>(options =>
    options.UseMySql(chatDbConnectionString, mySqlServerVersion));

builder.Services.AddScoped<IChatRepository, ChatRepository>();

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
            // Fully qualified because SocketWeb.Data.ChatMessage (the
            // database entity added for persistence) now also shares this
            // project - this /ws handler still speaks the original flat
            // protocol until it's migrated to the typed one in a later phase.
            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var chatMessage = JsonSerializer.Deserialize<SocketShared.ChatMessage>(json);

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
