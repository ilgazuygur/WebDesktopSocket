using Microsoft.Extensions.Configuration;

namespace SocketDesktop.Core;

// Builds DesktopClientOptions from configuration for the desktop client:
// non-secret defaults in appsettings.json (Ai:BaseUrl, Ai:Model, optionally
// Socket:Url) plus environment-variable overrides. The API key is read ONLY
// from the AI_API_KEY environment variable and is never persisted, logged,
// or put in any committed file.
public static class DesktopConfiguration
{
    public static DesktopClientOptions Load()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            // Maps double-underscore env vars to config keys, e.g.
            // Ai__BaseUrl -> Ai:BaseUrl, Socket__Url -> Socket:Url.
            .AddEnvironmentVariables()
            .Build();

        return FromConfiguration(configuration);
    }

    // Exposed separately so tests can build options from an in-memory
    // configuration without touching real files or environment variables.
    public static DesktopClientOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new DesktopClientOptions();

        // BaseUrl / Model (non-secret) from the Ai section.
        configuration.GetSection("Ai").Bind(options.Ai);

        // API key strictly from the AI_API_KEY environment variable
        // (AddEnvironmentVariables surfaces it as the "AI_API_KEY" key).
        var apiKey = configuration["AI_API_KEY"] ?? Environment.GetEnvironmentVariable("AI_API_KEY");
        options.Ai.ApiKey = apiKey ?? string.Empty;

        // SocketWeb URL from Socket:Url (Socket__Url env or appsettings),
        // falling back to the local default.
        var socketUrl = configuration["Socket:Url"];
        if (!string.IsNullOrWhiteSpace(socketUrl))
        {
            options.SocketUrl = socketUrl;
        }

        return options;
    }
}
