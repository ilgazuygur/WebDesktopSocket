using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SocketDesktop.Services;
using SocketShared;

namespace SocketDesktop;

public partial class MainWindow : Window
{
    private readonly SocketClientService _socketClient = new();

    // Bound to the ItemsControl in MainWindow.xaml, which renders each
    // ChatMessage as a chat bubble using the DataTemplate defined there.
    // ObservableCollection automatically tells the UI to redraw itself
    // whenever an item is added.
    private readonly ObservableCollection<ChatMessage> _log = new();

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _log;

        // Whenever a message arrives from the server (originally from the
        // web page or from this app), add it to the log.
        _socketClient.MessageReceived += message =>
        {
            // Events can arrive on a background thread, but WPF UI
            // elements can only be touched from the UI thread, so we hop
            // back onto it with Dispatcher.Invoke.
            Dispatcher.Invoke(() =>
            {
                _log.Add(message);
                LogScroll.ScrollToEnd();
            });
        };

        _socketClient.ConnectionStatusChanged += isConnected =>
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = isConnected ? "Connected" : "Disconnected";

                var dotColor = isConnected ? Color.FromRgb(0x34, 0xC7, 0x59) : Color.FromRgb(0xFF, 0x3B, 0x30);
                var textColor = isConnected ? Color.FromRgb(0x1A, 0x7F, 0x37) : Color.FromRgb(0xB3, 0x26, 0x1E);

                StatusDot.Fill = new SolidColorBrush(dotColor);
                StatusText.Foreground = new SolidColorBrush(textColor);
            });
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _socketClient.ConnectAsync();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendCurrentMessageAsync();
    }

    private async void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SendCurrentMessageAsync();
        }
    }

    private async Task SendCurrentMessageAsync()
    {
        var text = MessageInput.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var message = new ChatMessage
        {
            Sender = "Desktop",
            Text = text,
            Timestamp = DateTime.Now
        };

        // We do NOT add this message to the log here. Instead we wait for
        // the server to broadcast it back to us via MessageReceived, so
        // the message shows up exactly once, the same way it does for
        // every other connected client.
        await _socketClient.SendAsync(message);

        MessageInput.Text = string.Empty;
    }
}
