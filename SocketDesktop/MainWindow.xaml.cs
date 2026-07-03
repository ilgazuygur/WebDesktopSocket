using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using SocketDesktop.Core;

namespace SocketDesktop;

// The legacy Windows-specific dashboard. All the real networking/AI logic
// now lives in SocketDesktop.Core.DesktopSocketClient (shared with the
// cross-platform SocketDesktop.Avalonia app); this window just renders its
// state and events onto the WPF UI.
public partial class MainWindow : Window
{
    private const int MaxLogEntries = 200;

    private readonly DesktopSocketClient _client;

    private readonly ObservableCollection<string> _activityLog = new();

    public MainWindow(DesktopSocketClient client, DesktopClientOptions options)
    {
        InitializeComponent();

        _client = client;
        ActivityLogList.ItemsSource = _activityLog;

        BaseUrlText.Text = string.IsNullOrWhiteSpace(options.Ai.BaseUrl) ? "(not set)" : options.Ai.BaseUrl;
        ModelText.Text = string.IsNullOrWhiteSpace(options.Ai.Model) ? "(not set)" : options.Ai.Model;
        SetApiKeyStatus(!string.IsNullOrWhiteSpace(options.Ai.ApiKey));

        if (!options.Ai.IsComplete)
        {
            AppendLog("AI configuration is incomplete - set Ai:BaseUrl / Ai:Model in appsettings.json and the AI_API_KEY environment variable. AiRequests will fail with a clear error until this is fixed.");
        }

        // Events can arrive on a background thread (the WebSocket receive
        // loop), but WPF UI elements can only be touched from the UI
        // thread, so every handler hops back onto it with Dispatcher.Invoke.
        _client.StateChanged += state => Dispatcher.Invoke(() => SetConnectionStatus(state));
        _client.ActivityLogged += message => Dispatcher.Invoke(() => AppendLog(message));
        _client.CurrentRequestChanged += status => Dispatcher.Invoke(() => SetCurrentRequestStatus(status));
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _client.Start();
    }

    private void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _client.Start();               // start the loop if it isn't running
        _client.RequestReconnectNow(); // otherwise force an immediate retry
    }

    private void SetConnectionStatus(DesktopConnectionState state)
    {
        switch (state)
        {
            case DesktopConnectionState.Connected:
                SetPill(WsStatusDot, WsStatusText, PillTone.Good, "Connected");
                SetPill(RegistrationStatusDot, RegistrationStatusText, PillTone.Good, "Registered as Desktop");
                break;
            case DesktopConnectionState.Connecting:
                SetPill(WsStatusDot, WsStatusText, PillTone.Warn, "Connecting…");
                SetPill(RegistrationStatusDot, RegistrationStatusText, PillTone.Bad, "Not registered");
                break;
            case DesktopConnectionState.Reconnecting:
                SetPill(WsStatusDot, WsStatusText, PillTone.Warn, "Reconnecting…");
                SetPill(RegistrationStatusDot, RegistrationStatusText, PillTone.Bad, "Not registered");
                break;
            default:
                SetPill(WsStatusDot, WsStatusText, PillTone.Bad, "Disconnected");
                SetPill(RegistrationStatusDot, RegistrationStatusText, PillTone.Bad, "Not registered");
                break;
        }
    }

    private void SetApiKeyStatus(bool hasKey)
    {
        SetPill(ApiKeyStatusDot, ApiKeyStatusText, hasKey ? PillTone.Good : PillTone.Bad, hasKey ? "Configured" : "Missing");
    }

    private void SetCurrentRequestStatus(string? status)
    {
        CurrentRequestText.Text = status ?? "Idle";
    }

    private void AppendLog(string message)
    {
        _activityLog.Add($"{DateTime.Now:HH:mm:ss}  {message}");

        while (_activityLog.Count > MaxLogEntries)
        {
            _activityLog.RemoveAt(0);
        }

        ActivityLogScroll.ScrollToEnd();
    }

    private enum PillTone { Good, Warn, Bad }

    private static void SetPill(Ellipse dot, System.Windows.Controls.TextBlock text, PillTone tone, string label)
    {
        text.Text = label;

        var (dotColor, textColor) = tone switch
        {
            PillTone.Good => (Color.FromRgb(0x34, 0xC7, 0x59), Color.FromRgb(0x1A, 0x7F, 0x37)),
            PillTone.Warn => (Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xB4, 0x54, 0x09)),
            _ => (Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xB3, 0x26, 0x1E))
        };

        dot.Fill = new SolidColorBrush(dotColor);
        text.Foreground = new SolidColorBrush(textColor);
    }
}
