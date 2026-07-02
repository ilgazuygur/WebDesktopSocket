using System.Net.Http;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SocketDesktop.Avalonia.ViewModels;
using SocketDesktop.Core;
using SocketDesktop.Core.Sockets;
using SocketShared.Ai;

namespace SocketDesktop.Avalonia;

public partial class App : Application
{
    private DesktopSocketClient? _client;
    private MainWindowViewModel? _viewModel;
    private HttpClient? _httpClient;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // --- Composition root (manual dependency injection) ---
            var options = DesktopConfiguration.Load();

            // SocketDesktop is the only place that calls the AI API; the
            // HttpClient timeout backs up the client's own per-request
            // timeout in SocketDesktop.Core.
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            IAiClient aiClient = new OpenAiCompatibleClient(_httpClient, options.Ai);

            _client = new DesktopSocketClient(aiClient, options, new RealClientWebSocketFactory());
            _viewModel = new MainWindowViewModel(_client, options);

            desktop.MainWindow = new MainWindow { DataContext = _viewModel };

            // Connect on launch (registration doesn't need the API key, so
            // this works even if AI config is incomplete - AiRequests then
            // fail with a clear error until it's fixed).
            _client.Start();

            // Clean shutdown: stop the loop, dispose the socket + HttpClient.
            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        _viewModel?.Dispose();
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
        _httpClient?.Dispose();
    }
}
