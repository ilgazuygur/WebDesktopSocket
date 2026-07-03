using System.Runtime.InteropServices;
using SocketDesktop.Avalonia.ViewModels;
using SocketDesktop.Core;
using SocketShared.Ai;
using SocketShared.Protocol;

namespace Socket.Tests.DesktopCore;

// Tests the Avalonia MainWindowViewModel over a real DesktopSocketClient
// wired to fakes. A synchronous UI-post delegate is injected so these run
// as plain xUnit tests without needing the Avalonia UI thread - but unlike
// the real app (where every post AND every read happens on the same
// Avalonia UI thread, so ordering is free), here the writes happen inline
// on DesktopSocketClient's background loop thread while the test reads
// from its own thread. Without synchronization, two properties set
// together in one post (e.g. IsProcessingRequest then CurrentRequestText)
// are not guaranteed to become visible to another thread in that order -
// a poll could see the second write but not the first. `gate` below is a
// lock shared between every post and every condition check, giving the
// test thread the same happens-before guarantee the real UI thread gets
// for free.
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

    private sealed record Fixture(MainWindowViewModel Vm, DesktopSocketClient Client, FakeClientWebSocket Socket, object Gate)
    {
        // Every read of vm/socket state in a test should go through here,
        // so it's synchronized with the same gate the post delegate uses.
        public Task Wait(Func<bool> condition) => WaitForAsync(() => { lock (Gate) { return condition(); } });

        public T Read<T>(Func<T> read) { lock (Gate) { return read(); } }

        public void Read(Action action) { lock (Gate) { action(); } }
    }

    private static Fixture Build(bool aiConfigured = true)
    {
        var gate = new object();
        var socket = new FakeClientWebSocket();
        var options = Options(aiConfigured);
        var client = new DesktopSocketClient(new FakeAiClient(), options, new FakeClientWebSocketFactory(socket));
        var vm = new MainWindowViewModel(client, options, post: action => { lock (gate) { action(); } });
        return new Fixture(vm, client, socket, gate);
    }

    // Evaluates `condition` at most once per loop iteration and returns the
    // instant it's true, rather than checking once in a loop guard and then
    // re-checking again afterward - two separate calls a few instructions
    // apart could observe different results for a condition backed by
    // mutable state written from another thread.
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var start = DateTime.UtcNow;
        while (true)
        {
            if (condition())
            {
                return;
            }
            if ((DateTime.UtcNow - start).TotalMilliseconds >= timeoutMs)
            {
                throw new TimeoutException("Condition not met within timeout.");
            }
            await Task.Delay(10);
        }
    }

    [Fact]
    public void InitialState_ShowsDisconnected_AndConfigDetails()
    {
        var f = Build();

        Assert.Equal("Disconnected", f.Vm.ConnectionStatusText);
        Assert.False(f.Vm.IsRegistered);
        Assert.Equal("https://api.example.test/v1", f.Vm.BaseUrl);
        Assert.Equal("test-model", f.Vm.Model);
        Assert.True(f.Vm.HasApiKey);
        Assert.Equal("Configured", f.Vm.ApiKeyStatusText);
        Assert.Equal("Ready", f.Vm.AiConfigSummary);
        // Cross-platform: assert the OS name actually matches whatever CI/dev
        // machine this runs on (macOS, Windows, or Linux), instead of assuming
        // a specific one - this test previously hardcoded "macOS" and failed
        // on the Windows CI runner.
        var expectedOs =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" : "Unknown OS";
        Assert.Contains(expectedOs, f.Vm.Platform, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApiKeyValue_IsNeverExposed_OnlyItsPresence()
    {
        var f = Build();

        // No property returns the actual key; only "Configured"/"Missing".
        Assert.Equal("Configured", f.Vm.ApiKeyStatusText);
        Assert.DoesNotContain("secret-key", f.Vm.AiConfigSummary);
        Assert.DoesNotContain("secret-key", f.Vm.BaseUrl + f.Vm.Model + f.Vm.ServerUrl);
    }

    [Fact]
    public void WhenAiNotConfigured_SummaryListsMissingKey_AndLogsWarning()
    {
        var f = Build(aiConfigured: false);

        Assert.Equal("Missing", f.Vm.ApiKeyStatusText);
        Assert.Contains("AI_API_KEY", f.Vm.AiConfigSummary);
        Assert.Contains(f.Vm.ActivityLog, line => line.Contains("Configuration incomplete"));
    }

    [Fact]
    public void Commands_InitialEnablement_ConnectEnabled_DisconnectDisabled()
    {
        var f = Build();

        Assert.True(f.Vm.ConnectCommand.CanExecute(null));
        Assert.False(f.Vm.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhenClientConnects_StatusBecomesConnected_AndRegistered_AndCommandsFlip()
    {
        var f = Build();

        f.Vm.ConnectCommand.Execute(null); // starts the client
        await f.Wait(() => f.Socket.ConnectWasCalled);
        f.Socket.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });

        await f.Wait(() => f.Vm.ConnectionStatusText == "Connected");
        f.Read(() =>
        {
            Assert.True(f.Vm.IsRegistered);
            Assert.Equal("Registered as Desktop", f.Vm.RegistrationText);
            Assert.False(f.Vm.ConnectCommand.CanExecute(null));   // already connected
            Assert.True(f.Vm.DisconnectCommand.CanExecute(null));
        });

        await f.Client.StopAsync();
    }

    [Fact]
    public async Task ActivityLog_ReceivesClientMessages()
    {
        var f = Build();

        f.Vm.ConnectCommand.Execute(null);
        await f.Wait(() => f.Vm.ActivityLog.Any(line => line.Contains("Connected to SocketWeb")));

        f.Read(() => Assert.Contains(f.Vm.ActivityLog, line => line.Contains("Connected to SocketWeb")));

        await f.Client.StopAsync();
    }

    [Fact]
    public async Task CurrentRequest_TogglesWhileProcessing_ThenBackToIdle()
    {
        var f = Build();

        f.Vm.ConnectCommand.Execute(null);
        await f.Wait(() => f.Socket.ConnectWasCalled);
        f.Socket.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });
        await f.Wait(() => f.Vm.IsRegistered);

        f.Socket.EnqueueMessage(new SocketMessage { Type = MessageType.AiRequest, SessionId = "s", RequestId = "req-1" });

        // It processes quickly with the FakeAiClient, then returns to Idle.
        // Reading IsProcessingRequest through the same gate as the wait
        // condition guarantees it reflects the same post as CurrentRequestText.
        await f.Wait(() => f.Vm.CurrentRequestText == "Idle" && f.Vm.ActivityLog.Any(l => l.Contains("req-1")));
        f.Read(() => Assert.False(f.Vm.IsProcessingRequest));

        await f.Client.StopAsync();
    }
}
