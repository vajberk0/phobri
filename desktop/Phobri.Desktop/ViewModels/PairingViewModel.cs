using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phobri.Desktop.Services;

namespace Phobri.Desktop.ViewModels;

/// <summary>
/// ViewModel for the pairing / setup flow.
/// </summary>
public partial class PairingViewModel : ViewModelBase
{
    private readonly IPairingService _pairingService;
    private readonly IExternalIpService _externalIpService;

    public PairingViewModel(IPairingService pairingService, IExternalIpService externalIpService)
    {
        _pairingService = pairingService;
        _externalIpService = externalIpService;
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
        }
        finally
        {
            IsGenerating = false;
        }
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
