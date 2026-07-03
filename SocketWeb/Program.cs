using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SocketWeb.Api;
using SocketWeb.Data;
using SocketWeb.Services;

var builder = WebApplication.CreateBuilder(args);

// Make sure the site always runs on the same, fixed port, so the desktop
// app always knows exactly where to connect.
builder.WebHost.UseUrls("http://localhost:5080");

builder.Services.AddRazorPages();

// The connection manager is a singleton: there is exactly one instance
// for the whole application, shared by every request, so it can keep
// track of every open WebSocket connection in one place.
builder.Services.AddSingleton<WebSocketConnectionManager>();

// ChatSocketHandler is also a singleton - it tracks pending AI requests
// across every connection for the app's whole lifetime. It reaches the
// (scoped) database via IServiceScopeFactory instead of taking
// IChatRepository directly - see the comment on the class itself.
builder.Services.AddSingleton<ChatSocketHandler>();

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

// A single WebSocket message larger than this is rejected and the
// connection is closed - a reasonable upper bound so a buggy or malicious
// client can't force the server to buffer unlimited memory for one
// message (conversation history in an AiRequest is the largest realistic
// payload, and this comfortably covers that for MaxPromptLength-sized
// prompts).
const int maxMessageBytes = 256 * 1024;

// This is the raw WebSocket endpoint. Both the browser's JavaScript
// WebSocket and the desktop app's ClientWebSocket connect here. All the
// actual routing/business logic (who this connection is, what to do with
// each message) lives in ChatSocketHandler - this loop's only job is
// framing: turn WebSocket frames into complete JSON messages.
app.Map("/ws", async (HttpContext context, WebSocketConnectionManager manager, ChatSocketHandler handler) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connectionId = manager.AddConnection(socket);

    var receiveBuffer = new byte[4 * 1024];

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            using var messageStream = new MemoryStream();
            WebSocketReceiveResult result;
            var tooLarge = false;

            // A single logical message can arrive across multiple frames
            // (multiple ReceiveAsync calls) - keep reading until
            // EndOfMessage, accumulating into messageStream, unless it
            // grows past the size cap first.
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), context.RequestAborted);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (messageStream.Length + result.Count > maxMessageBytes)
                {
                    tooLarge = true;
                    break;
                }

                messageStream.Write(receiveBuffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                break;
            }

            if (tooLarge)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", CancellationToken.None);
                break;
            }

            var json = Encoding.UTF8.GetString(messageStream.ToArray());
            await handler.HandleMessageAsync(connectionId, json, context.RequestAborted);
        }
    }
    catch (WebSocketException)
    {
        // Happens when a client disconnects abruptly (e.g. app closed).
        // Nothing special to do — we just clean up below.
    }
    catch (OperationCanceledException)
    {
        // Server shutting down, or the underlying request was aborted.
    }
    finally
    {
        await manager.RemoveConnectionAsync(connectionId);
    }
});

// Plain HTTP CRUD for chat sessions/messages (create/list/load/rename/
// delete) - a separate concern from the real-time /ws AI flow above.
app.MapSessionEndpoints();

app.MapRazorPages();

app.Run();

// Minimal API top-level Program.cs files get an internal Program class by
// default, which Socket.Tests (an external test project) can't see. This
// makes it public so Microsoft.AspNetCore.Mvc.Testing's
// WebApplicationFactory<Program> can boot the app for endpoint tests.
public partial class Program { }
