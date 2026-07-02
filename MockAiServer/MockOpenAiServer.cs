using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MockAiServer;

// A deterministic, OpenAI-compatible HTTP server for testing - it accepts
// the same POST /v1/chat/completions request the real OpenAiCompatibleClient
// sends and returns a fixed, recognizable assistant reply. It exists ONLY
// as test/development infrastructure and never replaces the production AI
// client. It ignores the Authorization header, so tests need no real key.
//
// Usable two ways: started in-process by a test (MockOpenAiServer.Start),
// or run as a standalone process (see Program.cs) that the real desktop app
// points at for end-to-end runs.
public sealed class MockOpenAiServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _reply;
    private readonly CancellationTokenSource _cts = new();
    private int _requestCount;
    private Task? _loop;

    public int Port { get; }

    // Matches how OpenAiCompatibleClient builds its endpoint: it POSTs to
    // "{BaseUrl}/chat/completions", so BaseUrl ends with "/v1".
    public string BaseUrl => $"http://localhost:{Port}/v1";

    public int RequestCount => Volatile.Read(ref _requestCount);

    private MockOpenAiServer(int port, string reply)
    {
        Port = port;
        _reply = reply;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public static MockOpenAiServer Start(int port, string reply)
    {
        var server = new MockOpenAiServer(port, reply);
        server._listener.Start();
        server._loop = Task.Run(() => server.AcceptLoopAsync(server._cts.Token));
        return server;
    }

    // Picks a free TCP port on loopback so parallel tests don't collide.
    public static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                break; // listener stopped
            }
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            var isCompletions = context.Request.HttpMethod == "POST"
                && path.EndsWith("chat/completions", StringComparison.OrdinalIgnoreCase);

            if (!isCompletions)
            {
                await WriteJsonAsync(context, 404, "{\"error\":\"not found\"}");
                return;
            }

            Interlocked.Increment(ref _requestCount);

            // Drain the request body (we don't need it - the reply is fixed -
            // and we never log it or the Authorization header, to keep any
            // real secret out of the logs).
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                _ = await reader.ReadToEndAsync();
            }

            var responseJson = JsonSerializer.Serialize(new
            {
                id = "mock-" + Guid.NewGuid().ToString("N"),
                @object = "chat.completion",
                model = "mock-model",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = _reply },
                        finish_reason = "stop"
                    }
                }
            });

            await WriteJsonAsync(context, 200, responseJson);
        }
        catch (Exception)
        {
            try { context.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int statusCode, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
            // Already stopped.
        }
    }
}
