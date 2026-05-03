using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phobri.Desktop.Infrastructure;
using Phobri.Desktop.Services;

namespace Phobri.Desktop.ViewModels;

/// <summary>
/// ViewModel for the pairing / setup flow.
/// Supports QR code generation for easy Android setup.
/// </summary>
public partial class PairingViewModel : ViewModelBase
{
    private readonly IPairingService _pairingService;
    private readonly IExternalIpService _externalIpService;
    private readonly IFcmPushService? _fcmPush;
    private readonly ConfigurationManager _config;

    public PairingViewModel(IPairingService pairingService, IExternalIpService externalIpService, ConfigurationManager config, IFcmPushService? fcmPush = null)
    {
        _pairingService = pairingService;
        _externalIpService = externalIpService;
        _fcmPush = fcmPush;
        _config = config;
        LocalAddresses = new ObservableCollection<string>();
        // Load saved FCM config path
        var savedConfig = _config.Load();
        if (!string.IsNullOrWhiteSpace(savedConfig.FcmServiceAccountPath))
        {
            _fcmServiceAccountPath = savedConfig.FcmServiceAccountPath;
            IsFcmReady = _fcmPush?.IsInitialized ?? false;
            FcmStatusText = IsFcmReady ? "✓ FCM ready from saved config" : "FCM not initialized";
        }
    }

    [ObservableProperty]
    private string? _pairingToken;

    [ObservableProperty]
    private string? _externalIp;

    [ObservableProperty]
    private string? _fingerprint;

    [ObservableProperty]
    private bool _isPaired;

    [ObservableProperty]
    private bool _isGenerating;

    /// <summary>PNG bytes for the QR code image. Null if no token generated yet.</summary>
    [ObservableProperty]
    private byte[]? _qrCodePng;

    /// <summary>Local network addresses detected on this machine.</summary>
    public ObservableCollection<string> LocalAddresses { get; }

    /// <summary>The selected local address to encode in the QR code.</summary>
    [ObservableProperty]
    private string? _selectedLocalAddress;

    /// <summary>The port used in the QR code URI.</summary>
    [ObservableProperty]
    private int _serverPort = 8765;

    /// <summary>Human-readable QR code URI (for display/debug).</summary>
    [ObservableProperty]
    private string? _qrCodeUri;

    /// <summary>Whether FCM push is initialized.</summary>
    [ObservableProperty]
    private bool _isFcmReady;

    /// <summary>FCM service account JSON key file path.</summary>
    [ObservableProperty]
    private string _fcmServiceAccountPath = "";

    /// <summary>Status text for FCM configuration.</summary>
    [ObservableProperty]
    private string _fcmStatusText = "";

    [RelayCommand]
    private async Task GeneratePairTokenAsync()
    {
        IsGenerating = true;
        try
        {
            PairingToken = _pairingService.GeneratePairingToken();
            Fingerprint = _pairingService.CertificateFingerprint;
            ExternalIp = await _externalIpService.GetExternalIpAsync();
            IsPaired = _pairingService.IsPaired;

            // Populate local addresses
            LocalAddresses.Clear();
            foreach (var addr in _pairingService.GetLocalAddresses())
                LocalAddresses.Add(addr);

            // Pick the first private address as default
            SelectedLocalAddress = LocalAddresses.FirstOrDefault();

            // Generate the QR code
            RegenerateQrCode();
        }
        finally
        {
            IsGenerating = false;
        }
    }

    [RelayCommand]
    private void RegenerateQrCode()
    {
        if (SelectedLocalAddress is not null)
        {
            QrCodePng = _pairingService.GenerateQrCode(SelectedLocalAddress, ServerPort);
            if (QrCodePng is not null)
            {
                QrCodeUri = $"phobri://pair?h={Uri.EscapeDataString(SelectedLocalAddress)}&p={ServerPort}&t={PairingToken}&f={Fingerprint}";
            }
        }
    }

    /// <summary>
    /// Called when the selected local address changes from the UI.
    /// </summary>
    partial void OnSelectedLocalAddressChanged(string? value)
    {
        if (value is not null)
            RegenerateQrCode();
    }

    [RelayCommand]
    private async Task RefreshExternalIpAsync()
    {
        ExternalIp = await _externalIpService.GetExternalIpAsync();
    }

    /// <summary>
    /// Configure FCM with a service account key file path.
    /// </summary>
    [RelayCommand]
    private void ConfigureFcm()
    {
        if (_fcmPush is null)
        {
            FcmStatusText = "FCM service not available";
            return;
        }

        if (string.IsNullOrWhiteSpace(FcmServiceAccountPath))
        {
            FcmStatusText = "Please enter the path to your Firebase service account JSON key file";
            return;
        }

        var ok = _fcmPush.Initialize(FcmServiceAccountPath);
        IsFcmReady = ok;
        FcmStatusText = ok
            ? "✓ FCM ready — push wake is active"
            : "✗ Failed to initialize FCM. Check the file path and format.";

        if (ok)
        {
            // Save the path to config so it auto-initializes on next start
            var config = _config.Load() with { FcmServiceAccountPath = FcmServiceAccountPath };
            _config.Save(config);
        }
    }

    /// <summary>
    /// Refresh the FCM status.
    /// </summary>
    public void RefreshFcmStatus()
    {
        if (_fcmPush is null)
        {
            IsFcmReady = false;
            FcmStatusText = "FCM service not available";
            return;
        }
        IsFcmReady = _fcmPush.IsInitialized;
        FcmStatusText = IsFcmReady ? "✓ FCM ready" : "FCM not configured";
    }

    [RelayCommand]
    private void Unpair()
    {
        _pairingService.Unpair();
        IsPaired = false;
        PairingToken = null;
        QrCodePng = null;
        QrCodeUri = null;
    }

    /// <summary>
    /// Refresh pairing state from the service.
    /// </summary>
    public void RefreshState()
    {
        IsPaired = _pairingService.IsPaired;
        Fingerprint = _pairingService.CertificateFingerprint;
    }
}
