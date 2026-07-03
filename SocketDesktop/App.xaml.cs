using System.Net.Http;
using System.Windows;
using SocketDesktop.Core;
using SocketDesktop.Core.Sockets;
using SocketShared.Ai;

namespace SocketDesktop;

public partial class App : Application
{
    // Manual composition root (constructor-injection style DI), matching
    // SocketDesktop.Avalonia. Configuration - including reading AI_API_KEY
    // only from the environment - is handled by the shared
    // SocketDesktop.Core.DesktopConfiguration, so both desktop clients load
    // it identically.
    private DesktopSocketClient? _client;
    private HttpClient? _httpClient;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = DesktopConfiguration.Load();

        // SocketDesktop is the only place that calls the AI API.
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        IAiClient aiClient = new OpenAiCompatibleClient(_httpClient, options.Ai);

        _client = new DesktopSocketClient(aiClient, options, new RealClientWebSocketFactory());

        var mainWindow = new MainWindow(_client, options);
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
        _httpClient?.Dispose();
        base.OnExit(e);
    }
}
