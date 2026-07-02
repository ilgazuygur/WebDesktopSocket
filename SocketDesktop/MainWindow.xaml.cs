using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using SocketDesktop.Services;
using SocketShared.Ai;

namespace SocketDesktop;

public partial class MainWindow : Window
{
    private const int MaxLogEntries = 200;

    private readonly DesktopSocketClient _socketClient;

    // Bound to the ItemsControl in MainWindow.xaml - each entry is one
    // already-formatted "HH:mm:ss  message" line.
    private readonly ObservableCollection<string> _activityLog = new();

    public MainWindow(DesktopSocketClient socketClient, AiOptions aiOptions)
    {
        InitializeComponent();

        _socketClient = socketClient;
        ActivityLogList.ItemsSource = _activityLog;

        BaseUrlText.Text = string.IsNullOrWhiteSpace(aiOptions.BaseUrl) ? "(not set)" : aiOptions.BaseUrl;
        ModelText.Text = string.IsNullOrWhiteSpace(aiOptions.Model) ? "(not set)" : aiOptions.Model;
        SetApiKeyStatus(!string.IsNullOrWhiteSpace(aiOptions.ApiKey));

        if (!aiOptions.IsComplete)
        {
            AppendLog("AI configuration is incomplete - set Ai:BaseUrl / Ai:Model in appsettings.json and the AI_API_KEY environment variable. AiRequests will fail with a clear error until this is fixed.");
        }

        // Events can arrive on a background thread (the WebSocket receive
        // loop), but WPF UI elements can only be touched from the UI
        // thread, so every handler hops back onto it with Dispatcher.Invoke.
        _socketClient.ConnectionStatusChanged += isConnected => Dispatcher.Invoke(() => SetConnectionStatus(isConnected));
        _socketClient.ActivityLogged += message => Dispatcher.Invoke(() => AppendLog(message));
        _socketClient.CurrentRequestChanged += status => Dispatcher.Invoke(() => SetCurrentRequestStatus(status));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _socketClient.ConnectAsync();
    }

    private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await _socketClient.ConnectAsync();
    }

    private void SetConnectionStatus(bool connected)
    {
        SetPill(WsStatusDot, WsStatusText, connected, connected ? "Connected" : "Disconnected");

        // Registration always tracks the WebSocket connection here - a
        // successful HelloAck is what raises ConnectionStatusChanged(true)
        // in the first place, and losing the connection means the
        // registration is gone with it.
        SetPill(RegistrationStatusDot, RegistrationStatusText, connected, connected ? "Registered as Desktop" : "Not registered");
    }

    private void SetApiKeyStatus(bool hasKey)
    {
        SetPill(ApiKeyStatusDot, ApiKeyStatusText, hasKey, hasKey ? "Configured" : "Missing");
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

    private static void SetPill(Ellipse dot, System.Windows.Controls.TextBlock text, bool positive, string label)
    {
        text.Text = label;

        var dotColor = positive ? Color.FromRgb(0x34, 0xC7, 0x59) : Color.FromRgb(0xFF, 0x3B, 0x30);
        var textColor = positive ? Color.FromRgb(0x1A, 0x7F, 0x37) : Color.FromRgb(0xB3, 0x26, 0x1E);

        dot.Fill = new SolidColorBrush(dotColor);
        text.Foreground = new SolidColorBrush(textColor);
    }
}
