using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public PairingViewModel(IPairingService pairingService, IExternalIpService externalIpService)
    {
        _pairingService = pairingService;
        _externalIpService = externalIpService;
        LocalAddresses = new ObservableCollection<string>();
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
