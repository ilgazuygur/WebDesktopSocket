using SocketDesktop.Avalonia.ViewModels;
using SocketDesktop.Core;
using SocketShared.Ai;
using SocketShared.Protocol;

namespace Socket.Tests.DesktopCore;

// Tests the Avalonia MainWindowViewModel over a real DesktopSocketClient
// wired to fakes. A synchronous UI-post delegate (a => a()) is injected so
// these run as plain xUnit tests without needing the Avalonia UI thread.
public class MainWindowViewModelTests
{
    private static DesktopClientOptions Options(bool aiConfigured = true) => new()
    {
        SocketUrl = "ws://localhost:5080/ws",
        InitialReconnectDelay = TimeSpan.FromMilliseconds(20),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(60),
        Ai = aiConfigured
            ? new AiOptions { BaseUrl = "https://api.example.test/v1", Model = "test-model", ApiKey = "secret-key" }
            : new AiOptions { BaseUrl = "https://api.example.test/v1", Model = "test-model", ApiKey = "" }
    };

    private static (MainWindowViewModel vm, DesktopSocketClient client, FakeClientWebSocket socket) Build(bool aiConfigured = true)
    {
        var socket = new FakeClientWebSocket();
        var options = Options(aiConfigured);
        var client = new DesktopSocketClient(new FakeAiClient(), options, new FakeClientWebSocketFactory(socket));
        var vm = new MainWindowViewModel(client, options, post: action => action()); // run inline, no UI thread
        return (vm, client, socket);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var start = DateTime.UtcNow;
        while (!condition() && (DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
        if (!condition())
        {
            throw new TimeoutException("Condition not met within timeout.");
        }
    }

    [Fact]
    public void InitialState_ShowsDisconnected_AndConfigDetails()
    {
        var (vm, _, _) = Build();

        Assert.Equal("Disconnected", vm.ConnectionStatusText);
        Assert.False(vm.IsRegistered);
        Assert.Equal("https://api.example.test/v1", vm.BaseUrl);
        Assert.Equal("test-model", vm.Model);
        Assert.True(vm.HasApiKey);
        Assert.Equal("Configured", vm.ApiKeyStatusText);
        Assert.Equal("Ready", vm.AiConfigSummary);
        Assert.Contains("macOS", vm.Platform + " ", StringComparison.OrdinalIgnoreCase); // platform indicator populated
    }

    [Fact]
    public void ApiKeyValue_IsNeverExposed_OnlyItsPresence()
    {
        var (vm, _, _) = Build();

        // No property returns the actual key; only "Configured"/"Missing".
        Assert.Equal("Configured", vm.ApiKeyStatusText);
        Assert.DoesNotContain("secret-key", vm.AiConfigSummary);
        Assert.DoesNotContain("secret-key", vm.BaseUrl + vm.Model + vm.ServerUrl);
    }

    [Fact]
    public void WhenAiNotConfigured_SummaryListsMissingKey_AndLogsWarning()
    {
        var (vm, _, _) = Build(aiConfigured: false);

        Assert.Equal("Missing", vm.ApiKeyStatusText);
        Assert.Contains("AI_API_KEY", vm.AiConfigSummary);
        Assert.Contains(vm.ActivityLog, line => line.Contains("Configuration incomplete"));
    }

    [Fact]
    public void Commands_InitialEnablement_ConnectEnabled_DisconnectDisabled()
    {
        var (vm, _, _) = Build();

        Assert.True(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhenClientConnects_StatusBecomesConnected_AndRegistered_AndCommandsFlip()
    {
        var (vm, client, socket) = Build();

        vm.ConnectCommand.Execute(null); // starts the client
        await WaitForAsync(() => socket.ConnectWasCalled);
        socket.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });

        await WaitForAsync(() => vm.ConnectionStatusText == "Connected");
        Assert.True(vm.IsRegistered);
        Assert.Equal("Registered as Desktop", vm.RegistrationText);
        Assert.False(vm.ConnectCommand.CanExecute(null));   // already connected
        Assert.True(vm.DisconnectCommand.CanExecute(null));

        await client.StopAsync();
    }

    [Fact]
    public async Task ActivityLog_ReceivesClientMessages()
    {
        var (vm, client, socket) = Build();

        vm.ConnectCommand.Execute(null);
        await WaitForAsync(() => vm.ActivityLog.Any(line => line.Contains("Connected to SocketWeb")));

        Assert.Contains(vm.ActivityLog, line => line.Contains("Connected to SocketWeb"));

        await client.StopAsync();
    }

    [Fact]
    public async Task CurrentRequest_TogglesWhileProcessing_ThenBackToIdle()
    {
        var (vm, client, socket) = Build();

        vm.ConnectCommand.Execute(null);
        await WaitForAsync(() => socket.ConnectWasCalled);
        socket.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });
        await WaitForAsync(() => vm.IsRegistered);

        socket.EnqueueMessage(new SocketMessage { Type = MessageType.AiRequest, SessionId = "s", RequestId = "req-1" });

        // It processes quickly with the FakeAiClient, then returns to Idle.
        await WaitForAsync(() => vm.CurrentRequestText == "Idle" && vm.ActivityLog.Any(l => l.Contains("req-1")));
        Assert.False(vm.IsProcessingRequest);

        await client.StopAsync();
    }
}
