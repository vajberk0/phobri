using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phobri.Desktop.Models;
using Phobri.Desktop.Services;

namespace Phobri.Desktop.ViewModels;

/// <summary>
/// ViewModel for call log display.
/// </summary>
public partial class CallLogViewModel : ViewModelBase
{
    private readonly IDataService _dataService;

    public CallLogViewModel(IDataService dataService)
    {
        _dataService = dataService;
    }

    [ObservableProperty]
    private ObservableCollection<CallLogEntry> _calls = [];

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Total call count across all types.
    /// </summary>
    [ObservableProperty]
    private int _totalCalls;

    /// <summary>
    /// Missed call count.
    /// </summary>
    [ObservableProperty]
    private int _missedCalls;

    [RelayCommand]
    private async Task LoadCallLogAsync()
    {
        IsLoading = true;
        try
        {
            Calls = new ObservableCollection<CallLogEntry>(
                await _dataService.GetCallLogAsync(limit: 200));

            TotalCalls = Calls.Count;
            MissedCalls = Calls.Count(c => c.Type == CallType.Missed);
        }
        catch (Exception ex)
        {
            Calls = [];
            TotalCalls = 0;
            MissedCalls = 0;
            App.ShowExceptionDialog("Load Call Log Error", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Add a new call entry (called from WebSocket push handler).
    /// </summary>
    public void AddCall(CallLogEntry entry)
    {
        Calls.Insert(0, entry);
        TotalCalls++;
        if (entry.Type == CallType.Missed)
            MissedCalls++;
    }
}
