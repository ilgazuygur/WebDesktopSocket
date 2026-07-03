using SocketShared.Ai;

namespace SocketDesktop.Core;

// Everything DesktopSocketClient needs to run, in one place: where
// SocketWeb is, and how to reach the AI provider. Built from configuration
// (environment variables + non-secret appsettings defaults) by the UI
// project - see SocketDesktop.Avalonia's composition root.
public sealed class DesktopClientOptions
{
    // The default local SocketWeb WebSocket endpoint - matches the fixed
    // URL SocketWeb listens on (see SocketWeb/Program.cs). Overridable via
    // the Socket__Url environment variable / configuration.
    public const string DefaultSocketUrl = "ws://localhost:5080/ws";

    public string SocketUrl { get; set; } = DefaultSocketUrl;

    public AiOptions Ai { get; set; } = new();

    // Reconnect tuning (bounded exponential backoff). Defaults are sensible
    // for local use; exposed so tests can use tiny delays.
    public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    // How long a single AI call may take before it's abandoned as a timeout,
    // independent of any HttpClient timeout, so a hung provider can never
    // leave a browser waiting forever.
    public TimeSpan AiRequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    // True once the SocketWeb URL is usable AND the AI config is complete.
    // The UI checks this (and the two more specific validators below) to
    // show accurate status before the user tries to connect.
    public bool IsFullyConfigured => IsSocketUrlValid && Ai.IsComplete;

    public bool IsSocketUrlValid =>
        Uri.TryCreate(SocketUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "ws" || uri.Scheme == "wss");

    // Human-readable list of what's missing, safe to show in the UI - never
    // includes the API key value itself, only whether it's present.
    public IReadOnlyList<string> DescribeMissingConfiguration()
    {
        var missing = new List<string>();
        if (!IsSocketUrlValid) missing.Add("a valid Socket__Url (ws:// or wss://)");
        if (string.IsNullOrWhiteSpace(Ai.BaseUrl)) missing.Add("Ai__BaseUrl");
        if (string.IsNullOrWhiteSpace(Ai.Model)) missing.Add("Ai__Model");
        if (string.IsNullOrWhiteSpace(Ai.ApiKey)) missing.Add("AI_API_KEY");
        return missing;
    }
}
