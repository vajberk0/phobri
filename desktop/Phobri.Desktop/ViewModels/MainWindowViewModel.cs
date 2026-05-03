using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public MainWindowViewModel(
        SyncServer syncServer,
        IWebSocketHandler wsHandler,
        IPairingService pairingService,
        IExternalIpService externalIpService,
        IDataService dataService,
        IUdpWakeService udpWakeService,
        IPasswordManagerService passwordManager,
        ILogService logService)
    {
        _syncServer = syncServer;
        _wsHandler = wsHandler;
        _pairingService = pairingService;
        _externalIpService = externalIpService;
        _dataService = dataService;
        _udpWakeService = udpWakeService;
        _passwordManager = passwordManager;

        SmsViewModel = new SmsViewModel(dataService);
        CallLogViewModel = new CallLogViewModel(dataService);
        PairingViewModel = new PairingViewModel(pairingService, externalIpService);
        LogViewModel = new LogViewModel(logService);

        // Subscribe to WebSocket events
        _wsHandler.SmsReceived += OnSmsReceived;
        _wsHandler.CallReceived += OnCallReceived;
        _wsHandler.ConnectionStateChanged += OnConnectionStateChanged;

        // Initial vault state
        RefreshVaultState();
    }

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
    /// Send a UDP wake packet to the phone.
    /// </summary>
    [RelayCommand]
    private async Task WakePhoneAsync(string phoneIp)
    {
        if (string.IsNullOrWhiteSpace(phoneIp)) return;

        var sent = await _udpWakeService.SendWakeAsync(phoneIp);
        StatusText = sent ? $"Wake packet sent to {phoneIp}" : $"Failed to send wake packet";
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

    /// <summary>
    /// Refresh the vault state properties from the password manager.
    /// </summary>
    public void RefreshVaultState()
    {
        IsVaultConfigured = _passwordManager.IsConfigured;
        IsVaultUnlocked = _passwordManager.IsUnlocked;
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
