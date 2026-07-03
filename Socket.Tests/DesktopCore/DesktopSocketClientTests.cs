using System.Diagnostics;
using SocketDesktop.Core;
using SocketShared.Ai;
using SocketShared.Protocol;

namespace Socket.Tests.DesktopCore;

// Drives DesktopSocketClient's connect/receive/reconnect loop against
// FakeClientWebSocket(s) and a FakeAiClient - no real network or AI. Uses
// very short reconnect delays so reconnect behavior can be verified fast.
public class DesktopSocketClientTests
{
    private static DesktopClientOptions Options(bool aiConfigured = true) => new()
    {
        SocketUrl = "ws://localhost:5080/ws",
        InitialReconnectDelay = TimeSpan.FromMilliseconds(20),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(80),
        AiRequestTimeout = TimeSpan.FromSeconds(5),
        Ai = aiConfigured
            ? new AiOptions { BaseUrl = "https://api.example.test/v1", Model = "test-model", ApiKey = "test-key" }
            : new AiOptions()
    };

    // Evaluates `condition` at most once per loop iteration and returns the
    // instant it's true, rather than checking once in a loop guard and then
    // re-checking again afterward - two separate calls a few instructions
    // apart could observe different results for a condition backed by
    // mutable state written from another thread (no synchronization),
    // turning a real success into a spurious "timeout".
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (condition())
            {
                return;
            }
            if (sw.ElapsedMilliseconds >= timeoutMs)
            {
                throw new TimeoutException("Condition not met within timeout.");
            }
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Start_ConnectsAndSendsClientHelloAsDesktop()
    {
        var socket = new FakeClientWebSocket();
        var client = new DesktopSocketClient(new FakeAiClient(), Options(), new FakeClientWebSocketFactory(socket));

        client.Start();
        await WaitForAsync(() => socket.SentSocketMessages.Any(m => m.Type == MessageType.ClientHello));

        var hello = socket.SentSocketMessages.First(m => m.Type == MessageType.ClientHello);
        Assert.Equal(ClientRole.Desktop, hello.Role);

        await client.StopAsync();
    }

    [Fact]
    public async Task OnHelloAck_TransitionsToConnected()
    {
        var socket = new FakeClientWebSocket();
        var client = new DesktopSocketClient(new FakeAiClient(), Options(), new FakeClientWebSocketFactory(socket));

        client.Start();
        await WaitForAsync(() => socket.ConnectWasCalled);
        socket.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });

        await WaitForAsync(() => client.State == DesktopConnectionState.Connected);
        Assert.Equal(DesktopConnectionState.Connected, client.State);

        await client.StopAsync();
    }

    [Fact]
    public async Task AiRequest_CallsAiClient_AndSendsAiResponseWithSameIds()
    {
        var socket = new FakeClientWebSocket();
        var ai = new FakeAiClient { Handler = (_, _) => Task.FromResult("Hello from the AI") };
        var client = new DesktopSocketClient(ai, Options(), new FakeClientWebSocketFactory(socket));

        client.Start();
        await WaitForAsync(() => socket.ConnectWasCalled);
        socket.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });
        await WaitForAsync(() => client.State == DesktopConnectionState.Connected);

        socket.EnqueueMessage(new SocketMessage
        {
            Type = MessageType.AiRequest,
            SessionId = "session-1",
            RequestId = "req-1",
            History = new List<ConversationTurn> { new() { Role = "user", Content = "Hi" } }
        });

        await WaitForAsync(() => socket.SentSocketMessages.Any(m => m.Type == MessageType.AiResponse));

        var response = socket.SentSocketMessages.First(m => m.Type == MessageType.AiResponse);
        Assert.Equal("session-1", response.SessionId);
        Assert.Equal("req-1", response.RequestId);
        Assert.Equal("Hello from the AI", response.Content);
        Assert.Equal(1, ai.CallCount);

        await client.StopAsync();
    }

    [Fact]
    public async Task AiRequest_WhenAiNotConfigured_SendsError_AndDoesNotCallAiClient()
    {
        var socket = new FakeClientWebSocket();
        var ai = new FakeAiClient();
        var client = new DesktopSocketClient(ai, Options(aiConfigured: false), new FakeClientWebSocketFactory(socket));

        client.Start();
        await WaitForAsync(() => socket.ConnectWasCalled);
        socket.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });
        await WaitForAsync(() => client.State == DesktopConnectionState.Connected);

        socket.EnqueueMessage(new SocketMessage { Type = MessageType.AiRequest, SessionId = "s", RequestId = "req-1" });

        await WaitForAsync(() => socket.SentSocketMessages.Any(m => m.Type == MessageType.Error));
        Assert.Equal(0, ai.CallCount);

        await client.StopAsync();
    }

    [Fact]
    public async Task AiRequest_DuplicateRequestId_IsHandledOnce()
    {
        var socket = new FakeClientWebSocket();
        var ai = new FakeAiClient { Handler = (_, _) => Task.FromResult("reply") };
        var client = new DesktopSocketClient(ai, Options(), new FakeClientWebSocketFactory(socket));

        client.Start();
        await WaitForAsync(() => socket.ConnectWasCalled);
        socket.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });
        await WaitForAsync(() => client.State == DesktopConnectionState.Connected);

        var request = new SocketMessage { Type = MessageType.AiRequest, SessionId = "s", RequestId = "dup" };
        socket.EnqueueMessage(request);
        socket.EnqueueMessage(request);

        await WaitForAsync(() => socket.SentSocketMessages.Count(m => m.Type == MessageType.AiResponse) >= 1);
        await Task.Delay(100); // give any (wrong) second handling a chance to show up

        Assert.Equal(1, ai.CallCount);
        Assert.Equal(1, socket.SentSocketMessages.Count(m => m.Type == MessageType.AiResponse));

        await client.StopAsync();
    }

    [Fact]
    public async Task AiRequest_OnAuthenticationFailure_SendsSpecificError()
    {
        var socket = new FakeClientWebSocket();
        var ai = new FakeAiClient { Handler = (_, _) => throw new AiAuthenticationException("bad key") };
        var client = new DesktopSocketClient(ai, Options(), new FakeClientWebSocketFactory(socket));

        client.Start();
        await WaitForAsync(() => socket.ConnectWasCalled);
        socket.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });
        await WaitForAsync(() => client.State == DesktopConnectionState.Connected);

        socket.EnqueueMessage(new SocketMessage { Type = MessageType.AiRequest, SessionId = "s", RequestId = "req-1" });

        await WaitForAsync(() => socket.SentSocketMessages.Any(m => m.Type == MessageType.Error));
        var error = socket.SentSocketMessages.First(m => m.Type == MessageType.Error);
        Assert.Contains("API key", error.Error);
        Assert.DoesNotContain("bad key", error.Error); // never forwards the raw exception message

        await client.StopAsync();
    }

    [Fact]
    public async Task Reconnect_AfterConnectionCloses_ReconnectsAndReRegisters()
    {
        var first = new FakeClientWebSocket();
        var second = new FakeClientWebSocket();
        var client = new DesktopSocketClient(new FakeAiClient(), Options(), new FakeClientWebSocketFactory(first, second));

        client.Start();
        await WaitForAsync(() => first.ConnectWasCalled);
        first.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });
        await WaitForAsync(() => client.State == DesktopConnectionState.Connected);

        // The server drops the first connection.
        first.EnqueueServerClose();

        // The client should create a second connection and re-register on it.
        await WaitForAsync(() => second.ConnectWasCalled);
        second.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });
        await WaitForAsync(() => client.State == DesktopConnectionState.Connected);

        Assert.Contains(second.SentSocketMessages, m => m.Type == MessageType.ClientHello);

        await client.StopAsync();
    }

    [Fact]
    public async Task Reconnect_WhenFirstConnectThrows_RetriesUntilConnected()
    {
        var failing = new FakeClientWebSocket { ConnectShouldThrow = true };
        var working = new FakeClientWebSocket();
        var client = new DesktopSocketClient(new FakeAiClient(), Options(), new FakeClientWebSocketFactory(failing, working));

        client.Start();

        await WaitForAsync(() => working.ConnectWasCalled);
        working.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });
        await WaitForAsync(() => client.State == DesktopConnectionState.Connected);

        Assert.True(failing.ConnectWasCalled);
        Assert.True(working.ConnectWasCalled);

        await client.StopAsync();
    }

    [Fact]
    public async Task StopAsync_StopsTheLoop_AndReportsDisconnected()
    {
        var socket = new FakeClientWebSocket();
        var client = new DesktopSocketClient(new FakeAiClient(), Options(), new FakeClientWebSocketFactory(socket));

        client.Start();
        await WaitForAsync(() => socket.ConnectWasCalled);
        socket.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });
        await WaitForAsync(() => client.State == DesktopConnectionState.Connected);

        await client.StopAsync();

        Assert.Equal(DesktopConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Start_IsIdempotent_DoesNotStartASecondLoop()
    {
        var socket = new FakeClientWebSocket();
        var client = new DesktopSocketClient(new FakeAiClient(), Options(), new FakeClientWebSocketFactory(socket));

        client.Start();
        client.Start(); // second call must be a no-op
        await WaitForAsync(() => socket.ConnectWasCalled);
        await Task.Delay(80);

        // Only one connection/ClientHello, proving there aren't two loops.
        Assert.Equal(1, socket.SentSocketMessages.Count(m => m.Type == MessageType.ClientHello));

        await client.StopAsync();
    }

    [Fact]
    public async Task DisposeAsync_CleansUpWithoutHanging()
    {
        var socket = new FakeClientWebSocket();
        var client = new DesktopSocketClient(new FakeAiClient(), Options(), new FakeClientWebSocketFactory(socket));

        client.Start();
        await WaitForAsync(() => socket.ConnectWasCalled);

        await client.DisposeAsync(); // should return promptly
        Assert.Equal(DesktopConnectionState.Disconnected, client.State);
    }
}
