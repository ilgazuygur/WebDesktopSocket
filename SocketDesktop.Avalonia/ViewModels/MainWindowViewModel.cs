using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Threading;
using SocketDesktop.Core;

namespace SocketDesktop.Avalonia.ViewModels;

// The dashboard view model. Wraps a DesktopSocketClient (all the real
// networking/AI logic lives in SocketDesktop.Core) and exposes bindable
// status/config/log properties plus Connect/Disconnect/Reconnect commands.
//
// The client raises its events on a background thread; every handler here
// marshals back onto the UI thread via the injected `post` delegate
// (Dispatcher.UIThread.Post in the real app; a synchronous inline call in
// unit tests) before touching any bindable state.
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogEntries = 200;

    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush AmberBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush RedBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush GreyBrush = new SolidColorBrush(Color.Parse("#9CA3AF"));

    private readonly DesktopSocketClient _client;
    private readonly DesktopClientOptions _options;
    private readonly Action<Action> _post;

    private string _connectionStatusText = "Disconnected";
    private IBrush _connectionStatusBrush = RedBrush;
    private bool _isRegistered;
    private string _currentRequestText = "Idle";
    private bool _isProcessingRequest;

    public MainWindowViewModel(DesktopSocketClient client, DesktopClientOptions options, Action<Action>? post = null)
    {
        _client = client;
        _options = options;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));

        Platform = $"{DescribeOs()} · {RuntimeInformation.OSArchitecture}";
        ServerUrl = options.SocketUrl;
        BaseUrl = string.IsNullOrWhiteSpace(options.Ai.BaseUrl) ? "(not set)" : options.Ai.BaseUrl;
        Model = string.IsNullOrWhiteSpace(options.Ai.Model) ? "(not set)" : options.Ai.Model;

        ConnectCommand = new RelayCommand(() => { _client.Start(); return Task.CompletedTask; },
            () => _client.State == DesktopConnectionState.Disconnected);
        DisconnectCommand = new RelayCommand(async () => await _client.StopAsync(),
            () => _client.State != DesktopConnectionState.Disconnected);
        ReconnectCommand = new RelayCommand(() =>
        {
            _client.Start();               // start the loop if it isn't running
            _client.RequestReconnectNow(); // otherwise force an immediate retry
            return Task.CompletedTask;
        });

        _client.StateChanged += OnStateChanged;
        _client.ActivityLogged += OnActivityLogged;
        _client.CurrentRequestChanged += OnCurrentRequestChanged;

        if (!_options.IsFullyConfigured)
        {
            var missing = string.Join(", ", _options.DescribeMissingConfiguration());
            AddLog($"Configuration incomplete - missing {missing}. Fix this before connecting.");
        }
    }

    // --- Static header info ---
    public string Title => "SocketDesktop · Avalonia";
    public string Platform { get; }
    public string ServerUrl { get; }

    // --- Connection / registration status ---
    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set => SetProperty(ref _connectionStatusText, value);
    }

    public IBrush ConnectionStatusBrush
    {
        get => _connectionStatusBrush;
        private set => SetProperty(ref _connectionStatusBrush, value);
    }

    public bool IsRegistered
    {
        get => _isRegistered;
        private set
        {
            if (SetProperty(ref _isRegistered, value))
            {
                OnPropertyChanged(nameof(RegistrationText));
                OnPropertyChanged(nameof(RegistrationBrush));
            }
        }
    }

    public string RegistrationText => IsRegistered ? "Registered as Desktop" : "Not registered";
    public IBrush RegistrationBrush => IsRegistered ? GreenBrush : GreyBrush;

    // --- AI configuration (never the key value itself) ---
    public string BaseUrl { get; }
    public string Model { get; }
    public bool HasApiKey => !string.IsNullOrWhiteSpace(_options.Ai.ApiKey);
    public string ApiKeyStatusText => HasApiKey ? "Configured" : "Missing";
    public IBrush ApiKeyStatusBrush => HasApiKey ? GreenBrush : RedBrush;

    public string AiConfigSummary => _options.Ai.IsComplete
        ? "Ready"
        : "Missing: " + string.Join(", ", _options.DescribeMissingConfiguration());
    public IBrush AiConfigBrush => _options.Ai.IsComplete ? GreenBrush : RedBrush;

    // --- Current AI request ---
    public string CurrentRequestText
    {
        get => _currentRequestText;
        private set => SetProperty(ref _currentRequestText, value);
    }

    public bool IsProcessingRequest
    {
        get => _isProcessingRequest;
        private set => SetProperty(ref _isProcessingRequest, value);
    }

    // --- Activity log ---
    public ObservableCollection<string> ActivityLog { get; } = new();

    // --- Commands ---
    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand ReconnectCommand { get; }

    private void OnStateChanged(DesktopConnectionState state) => _post(() =>
    {
        (ConnectionStatusText, ConnectionStatusBrush) = state switch
        {
            DesktopConnectionState.Connected => ("Connected", GreenBrush),
            DesktopConnectionState.Connecting => ("Connecting…", AmberBrush),
            DesktopConnectionState.Reconnecting => ("Reconnecting…", AmberBrush),
            _ => ("Disconnected", RedBrush)
        };
        IsRegistered = state == DesktopConnectionState.Connected;

        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
    });

    private void OnActivityLogged(string message) => _post(() => AddLog(message));

    private void OnCurrentRequestChanged(string? status) => _post(() =>
    {
        IsProcessingRequest = status is not null;
        CurrentRequestText = status ?? "Idle";
    });

    private void AddLog(string message)
    {
        ActivityLog.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (ActivityLog.Count > MaxLogEntries)
        {
            ActivityLog.RemoveAt(0);
        }
    }

    private static string DescribeOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
        return "Unknown OS";
    }

    public void Dispose()
    {
        _client.StateChanged -= OnStateChanged;
        _client.ActivityLogged -= OnActivityLogged;
        _client.CurrentRequestChanged -= OnCurrentRequestChanged;
    }
}
