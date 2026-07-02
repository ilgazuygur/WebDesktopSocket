using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SocketDesktop.Services;
using SocketShared.Ai;

namespace SocketDesktop;

public partial class App : Application
{
    // The generic host gives this WPF app the same dependency injection /
    // configuration / lifetime story ASP.NET Core apps use - configuration
    // is loaded from appsettings.json + environment variables, and
    // disposing the host on exit cleanly disposes everything registered in
    // it (including DesktopSocketClient's WebSocket connection).
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                var aiOptions = new AiOptions();
                context.Configuration.GetSection("Ai").Bind(aiOptions);

                // Deliberately read directly from the environment variable
                // (not through configuration section binding) so the name
                // matches exactly what README.md / .env.example document:
                // AI_API_KEY. Never written to appsettings.json, logged, or
                // included in any exception message anywhere in this app.
                aiOptions.ApiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? string.Empty;
                services.AddSingleton(aiOptions);

                // SocketDesktop is the only project in the solution that
                // creates or calls IAiClient - SocketWeb never registers it.
                services.AddHttpClient<IAiClient, OpenAiCompatibleClient>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(60);
                });

                services.AddSingleton<DesktopSocketClient>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
