using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SocketDesktop.Avalonia;
using SocketDesktop.Avalonia.ViewModels;
using SocketDesktop.Core;
using SocketShared.Ai;
using SocketShared.Protocol;

namespace SocketDesktop.Avalonia.Tests;

// Real Avalonia headless UI tests: they build the actual MainWindow with a
// real view model and assert the XAML bindings and command wiring behave -
// things only a rendered control tree can verify (button enablement from
// command CanExecute, bound text, live updates on state change).
public class MainWindowHeadlessTests
{
    private static DesktopClientOptions Options() => new()
    {
        SocketUrl = "ws://localhost:5080/ws",
        InitialReconnectDelay = TimeSpan.FromMilliseconds(20),
        Ai = new AiOptions { BaseUrl = "https://api.example.test/v1", Model = "headless-model", ApiKey = "key" }
    };

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var start = DateTime.UtcNow;
        while (!condition() && (DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        if (!condition())
        {
            throw new TimeoutException("Condition not met within timeout.");
        }
    }

    [AvaloniaFact]
    public void Window_RendersWithViewModel_AndShowsBoundConfig()
    {
        var options = Options();
        var client = new DesktopSocketClient(new NoAiClient(), options, new HeadlessTestSocketFactory(new HeadlessTestSocket()));
        var vm = new MainWindowViewModel(client, options);
        var window = new MainWindow { DataContext = vm };
        window.Show();

        // The bound BaseUrl / Model text appears somewhere in the rendered tree.
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("https://api.example.test/v1", texts);
        Assert.Contains("headless-model", texts);
    }

    [AvaloniaFact]
    public void Buttons_ReflectCommandCanExecute_Initially()
    {
        var options = Options();
        var client = new DesktopSocketClient(new NoAiClient(), options, new HeadlessTestSocketFactory(new HeadlessTestSocket()));
        var vm = new MainWindowViewModel(client, options);
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var connect = window.FindControl<Button>("ConnectButton");
        var disconnect = window.FindControl<Button>("DisconnectButton");

        Assert.NotNull(connect);
        Assert.NotNull(disconnect);
        // Disconnected initially: Connect is available, Disconnect is not.
        Assert.True(connect!.IsEffectivelyEnabled);
        Assert.False(disconnect!.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task ClickingConnect_ThenRegistering_UpdatesStatusText_AndFlipsButtons()
    {
        var options = Options();
        var socket = new HeadlessTestSocket();
        var client = new DesktopSocketClient(new NoAiClient(), options, new HeadlessTestSocketFactory(socket));
        var vm = new MainWindowViewModel(client, options);
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var connect = window.FindControl<Button>("ConnectButton")!;
        var disconnect = window.FindControl<Button>("DisconnectButton")!;

        // Click Connect, then let the fake server complete registration.
        connect.Command!.Execute(null);
        socket.EnqueueMessage(new SocketMessage { Type = MessageType.HelloAck });

        await WaitForAsync(() => vm.ConnectionStatusText == "Connected");

        Assert.True(vm.IsRegistered);
        Assert.False(connect.IsEffectivelyEnabled);   // can't connect again
        Assert.True(disconnect.IsEffectivelyEnabled);  // can now disconnect

        await client.StopAsync();
    }

    // A no-op IAiClient - these UI tests never exercise an AI request.
    private sealed class NoAiClient : IAiClient
    {
        public Task<string> CompleteAsync(IReadOnlyList<ConversationTurn> messages, CancellationToken cancellationToken = default)
            => Task.FromResult("unused");
    }
}
