using System.Text.Json;
using SocketShared.Protocol;
using SocketWeb.Services;

namespace Socket.Tests.Sockets;

public class WebSocketConnectionManagerTests
{
    [Fact]
    public void AddConnection_ReturnsUniqueServerGeneratedIds()
    {
        var manager = new WebSocketConnectionManager();

        var idA = manager.AddConnection(new FakeWebSocket());
        var idB = manager.AddConnection(new FakeWebSocket());

        Assert.NotEqual(idA, idB);
        Assert.NotNull(manager.GetConnection(idA));
        Assert.NotNull(manager.GetConnection(idB));
    }

    [Fact]
    public void SetRole_Desktop_MakesItTheActiveDesktopClient()
    {
        var manager = new WebSocketConnectionManager();
        var desktopId = manager.AddConnection(new FakeWebSocket());

        Assert.False(manager.IsDesktopOnline);
        manager.SetRole(desktopId, ClientRole.Desktop);

        Assert.True(manager.IsDesktopOnline);
    }

    [Fact]
    public async Task SetRole_Desktop_NotifiesExistingBrowsersOnline()
    {
        var manager = new WebSocketConnectionManager();
        var browserSocket = new FakeWebSocket();
        var browserId = manager.AddConnection(browserSocket);
        manager.SetRole(browserId, ClientRole.Browser);

        var desktopId = manager.AddConnection(new FakeWebSocket());
        manager.SetRole(desktopId, ClientRole.Desktop);

        // The notification is fire-and-forget from SetRole's perspective -
        // give it a moment to land.
        await Task.Delay(50);

        var statusMessage = Assert.Single(browserSocket.SentMessages);
        var parsed = JsonSerializer.Deserialize<SocketMessage>(statusMessage)!;
        Assert.Equal(MessageType.Status, parsed.Type);
        Assert.Equal(SocketStatusCodes.DesktopOnline, parsed.Content);
    }

    [Fact]
    public async Task SetRole_SecondDesktop_ReplacesTheActiveOne()
    {
        var manager = new WebSocketConnectionManager();
        var socketA = new FakeWebSocket();
        var idA = manager.AddConnection(socketA);
        manager.SetRole(idA, ClientRole.Desktop);

        var socketB = new FakeWebSocket();
        var idB = manager.AddConnection(socketB);
        manager.SetRole(idB, ClientRole.Desktop);

        var sent = await manager.SendToActiveDesktopAsync("""{"hello":"world"}""");

        Assert.True(sent);
        Assert.Empty(socketA.SentMessages);
        Assert.Single(socketB.SentMessages);
    }

    [Fact]
    public async Task RemoveConnectionAsync_ActiveDesktop_NotifiesBrowsersOffline_AndClearsActiveDesktop()
    {
        var manager = new WebSocketConnectionManager();
        var browserSocket = new FakeWebSocket();
        var browserId = manager.AddConnection(browserSocket);
        manager.SetRole(browserId, ClientRole.Browser);

        var desktopId = manager.AddConnection(new FakeWebSocket());
        manager.SetRole(desktopId, ClientRole.Desktop);
        await Task.Delay(20); // let the "online" notification land first

        await manager.RemoveConnectionAsync(desktopId);
        await Task.Delay(20);

        Assert.False(manager.IsDesktopOnline);
        var lastMessage = browserSocket.SentMessages[^1];
        var parsed = JsonSerializer.Deserialize<SocketMessage>(lastMessage)!;
        Assert.Equal(SocketStatusCodes.DesktopOffline, parsed.Content);
    }

    [Fact]
    public async Task RemoveConnectionAsync_NonActiveConnection_DoesNotAffectDesktopStatus()
    {
        var manager = new WebSocketConnectionManager();
        var desktopId = manager.AddConnection(new FakeWebSocket());
        manager.SetRole(desktopId, ClientRole.Desktop);

        var browserId = manager.AddConnection(new FakeWebSocket());
        manager.SetRole(browserId, ClientRole.Browser);

        await manager.RemoveConnectionAsync(browserId);

        Assert.True(manager.IsDesktopOnline);
    }

    [Fact]
    public async Task SendAsync_UnknownConnectionId_ReturnsFalse()
    {
        var manager = new WebSocketConnectionManager();

        var result = await manager.SendAsync(Guid.NewGuid().ToString(), "hi");

        Assert.False(result);
    }

    [Fact]
    public async Task SendAsync_ClosedSocket_ReturnsFalse()
    {
        var manager = new WebSocketConnectionManager();
        var socket = new FakeWebSocket();
        socket.SetState(System.Net.WebSockets.WebSocketState.Closed);
        var id = manager.AddConnection(socket);

        var result = await manager.SendAsync(id, "hi");

        Assert.False(result);
    }

    [Fact]
    public async Task SendToActiveDesktopAsync_NoDesktopOnline_ReturnsFalse()
    {
        var manager = new WebSocketConnectionManager();

        var result = await manager.SendToActiveDesktopAsync("hi");

        Assert.False(result);
    }

    [Fact]
    public async Task SendAsync_ConcurrentCalls_AreSerializedSafely()
    {
        var manager = new WebSocketConnectionManager();
        var socket = new FakeWebSocket { SendDelay = TimeSpan.FromMilliseconds(20) };
        var id = manager.AddConnection(socket);

        // If WebSocketConnectionManager didn't serialize sends through a
        // per-connection lock, these overlapping calls would make
        // FakeWebSocket throw InvalidOperationException (mirroring the
        // real WebSocket type's "already one outstanding SendAsync"
        // behavior).
        var sends = Enumerable.Range(0, 10).Select(i => manager.SendAsync(id, $"message-{i}"));
        var results = await Task.WhenAll(sends);

        Assert.All(results, Assert.True);
        Assert.Equal(10, socket.SentMessages.Count);
    }
}
