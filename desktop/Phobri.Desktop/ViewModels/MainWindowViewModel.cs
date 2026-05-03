using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phobri.Desktop.Infrastructure;
using Phobri.Desktop.Models;
using Phobri.Desktop.Services;

namespace Phobri.Desktop.ViewModels;

/// <summary>
/// Main window ViewModel. Orchestrates the overall app state,
/// server lifecycle, and tab navigation.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly SyncServer _syncServer;
    private readonly IWebSocketHandler _wsHandler;
    private readonly IPairingService _pairingService;
    private readonly IExternalIpService _externalIpService;
    private readonly IDataService _dataService;
    private readonly IUdpWakeService _udpWakeService;
    private readonly IPasswordManagerService _passwordManager;
    private readonly IFcmPushService? _fcmPush;
    private readonly ConfigurationManager _config;

    public MainWindowViewModel(
        SyncServer syncServer,
        IWebSocketHandler wsHandler,
        IPairingService pairingService,
        IExternalIpService externalIpService,
        IDataService dataService,
        IUdpWakeService udpWakeService,
        IPasswordManagerService passwordManager,
        ILogService logService,
        ConfigurationManager config,
        IFcmPushService? fcmPush = null)
    {
        _syncServer = syncServer;
        _wsHandler = wsHandler;
        _pairingService = pairingService;
        _externalIpService = externalIpService;
        _dataService = dataService;
        _udpWakeService = udpWakeService;
        _passwordManager = passwordManager;
        _fcmPush = fcmPush;
        _config = config;

        SmsViewModel = new SmsViewModel(dataService, wsHandler);
        CallLogViewModel = new CallLogViewModel(dataService, wsHandler);
        PairingViewModel = new PairingViewModel(pairingService, externalIpService, config, fcmPush);
        LogViewModel = new LogViewModel(logService);

        // Subscribe to WebSocket events
        _wsHandler.SmsReceived += OnSmsReceived;
        _wsHandler.CallReceived += OnCallReceived;
        _wsHandler.ConnectionStateChanged += OnConnectionStateChanged;

        // Auto-prompt for password when phone request hits locked server
        _syncServer.LockedRequestReceived += OnLockedRequestReceived;

        // Initial vault state
        RefreshVaultState();
    }

    /// <summary>
    /// Guard against multiple concurrent unlock dialogs.
    /// </summary>
    private bool _isPasswordDialogShowing;

    public SmsViewModel SmsViewModel { get; }
    public CallLogViewModel CallLogViewModel { get; }
    public PairingViewModel PairingViewModel { get; }
    public LogViewModel LogViewModel { get; }

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string? _externalIp;

    [ObservableProperty]
    private bool _isVaultConfigured;

    [ObservableProperty]
    private bool _isVaultUnlocked;

    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// Phone IP address for UDP wake. Configurable in Settings.
    /// </summary>
    [ObservableProperty]
    private string _phoneIp = "";

    // --- Computed properties for toolbar button visibility ---

    public bool ShowLockButton => IsVaultConfigured && IsVaultUnlocked;
    public bool ShowUnlockButton => IsVaultConfigured && !IsVaultUnlocked;
    public bool ShowStartButton => !IsServerRunning;
    public bool ShowStopButton => IsServerRunning;

    public string ConnectionStatusText => IsConnected ? "🟢 Connected" : "⚫ Disconnected";
    public string ServerStatusText => IsServerRunning ? "● Running" : "● Stopped";

    /// <summary>
    /// Start the sync server.
    /// </summary>
    [RelayCommand]
    private async Task StartServerAsync()
    {
        StatusText = "Starting server...";
        try
        {
            await _syncServer.StartAsync();
            IsServerRunning = true;
            ExternalIp = await _externalIpService.GetExternalIpAsync();
            StatusText = $"Server running on port {_syncServer.Port}. Waiting for connection...";
            PairingViewModel.RefreshState();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to start server: {ex.Message}";
        }
    }

    /// <summary>
    /// Stop the sync server.
    /// </summary>
    [RelayCommand]
    private async Task StopServerAsync()
    {
        await _syncServer.StopAsync();
        IsServerRunning = false;
        IsConnected = false;
        StatusText = "Server stopped";
    }

    /// <summary>
    /// Send a wake signal to the phone.
    /// Tries UDP first (LAN only), then FCM push (works anywhere).
    /// </summary>
    [RelayCommand]
    private async Task WakePhoneAsync(string? phoneIp)
    {
        var results = new List<string>();

        // 1) Try UDP wake on LAN
        if (!string.IsNullOrWhiteSpace(phoneIp))
        {
            var udpSent = await _udpWakeService.SendWakeAsync(phoneIp);
            results.Add(udpSent ? $"UDP wake sent to {phoneIp}" : "UDP wake failed");
        }

        // 2) Try FCM push (works over internet, even if phone is on cellular)
        if (_fcmPush is not null && _fcmPush.IsInitialized)
        {
            var fcmToken = _fcmPush.GetStoredFcmToken();
            if (!string.IsNullOrWhiteSpace(fcmToken))
            {
                // Pick the best server address: external IP first, then first local address
                var serverHost = ExternalIp
                    ?? _pairingService.GetLocalAddresses().FirstOrDefault()
                    ?? "localhost";

                var fcmSent = await _fcmPush.SendWakeAsync(
                    fcmToken, serverHost, _syncServer.Port);
                results.Add(fcmSent ? $"FCM wake sent (host={serverHost})" : "FCM wake failed");
            }
            else
            {
                results.Add("FCM: no phone token received yet");
            }
        }
        else
        {
            results.Add("FCM: not configured (set service account key in Settings)");
        }

        StatusText = string.Join(" | ", results);
    }

    /// <summary>
    /// Navigate to the Messages tab and load conversations.
    /// </summary>
    [RelayCommand]
    private async Task NavigateToMessagesAsync()
    {
        SelectedTabIndex = 0;
        await SmsViewModel.LoadMessagesCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Navigate to the Calls tab and load the call log.
    /// </summary>
    [RelayCommand]
    private async Task NavigateToCallsAsync()
    {
        SelectedTabIndex = 1;
        await CallLogViewModel.LoadCallLogCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Refresh external IP address.
    /// </summary>
    [RelayCommand]
    private async Task RefreshExternalIpAsync()
    {
        ExternalIp = await _externalIpService.GetExternalIpAsync();
        StatusText = ExternalIp is not null
            ? $"External IP: {ExternalIp}"
            : "Could not detect external IP";
    }

    /// <summary>
    /// Lock the vault manually.
    /// </summary>
    [RelayCommand]
    private async Task LockVaultAsync()
    {
        _passwordManager.Lock();
        await _dataService.LockAsync();
        RefreshVaultState();
        StatusText = "Vault locked";
    }

    /// <summary>
    /// Show unlock dialog.
    /// </summary>
    [RelayCommand]
    private async Task UnlockVaultAsync()
    {
        if (_isPasswordDialogShowing)
            return;

        _isPasswordDialogShowing = true;
        try
        {
            var unlockDialog = new Views.PasswordDialog(_passwordManager, isSetupMode: false);
            // Find the main window to use as owner
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (mainWindow is null) return;

            await unlockDialog.ShowDialog(mainWindow);
            if (unlockDialog.Success)
            {
                App.UnlockDatabase(_dataService, _passwordManager);
                RefreshVaultState();
                StatusText = "Vault unlocked";
            }
        }
        finally
        {
            _isPasswordDialogShowing = false;
        }
    }

    /// <summary>
    /// Handler for when the server rejects a phone request because the vault is locked.
    /// Automatically shows the unlock dialog so the next retry succeeds.
    /// </summary>
    private void OnLockedRequestReceived(object? sender, EventArgs e)
    {
        // Already unlocked or dialog already showing — nothing to do
        if (_passwordManager.IsUnlocked || _isPasswordDialogShowing)
            return;

        // Dispatch to UI thread (the event fires from a Kestrel worker thread)
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_passwordManager.IsUnlocked && !_isPasswordDialogShowing)
                UnlockVaultCommand.Execute(null);
        });
    }

    /// <summary>
    /// Navigate to the Settings tab.
    /// </summary>
    [RelayCommand]
    private void NavigateToSettings()
    {
        SelectedTabIndex = 3;
    }

    /// <summary>
    /// Refresh the vault state properties from the password manager.
    /// </summary>
    public void RefreshVaultState()
    {
        IsVaultConfigured = _passwordManager.IsConfigured;
        IsVaultUnlocked = _passwordManager.IsUnlocked;
        OnPropertyChanged(nameof(ShowLockButton));
        OnPropertyChanged(nameof(ShowUnlockButton));
    }

    // --- Partial OnChanged handlers to cascade computed property notifications ---

    partial void OnIsServerRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(ShowStopButton));
        OnPropertyChanged(nameof(ServerStatusText));
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionStatusText));
    }

    partial void OnIsVaultConfiguredChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLockButton));
        OnPropertyChanged(nameof(ShowUnlockButton));
    }

    partial void OnIsVaultUnlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLockButton));
        OnPropertyChanged(nameof(ShowUnlockButton));
    }

    // --- Event Handlers ---

    private void OnSmsReceived(object? sender, SmsMessage message)
    {
        // Dispatch to UI thread via Avalonia
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SmsViewModel.AddMessage(message);
            StatusText = $"New SMS from {message.DisplayName}";
        });
    }

    private void OnCallReceived(object? sender, CallLogEntry entry)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CallLogViewModel.AddCall(entry);
            StatusText = $"New call: {entry.DisplayName} ({entry.Type})";
        });
    }

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConnected = connected;
            StatusText = connected
                ? "Phone connected ✅"
                : "Waiting for phone connection...";
        });
    }
}
