using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phobri.Desktop.Services;

namespace Phobri.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Log tab. Exposes a real-time event log from ILogService.
/// </summary>
public partial class LogViewModel : ViewModelBase
{
    private readonly ILogService _logService;

    /// <summary>All log entries (newest first).</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    [ObservableProperty]
    private string? _filterText;

    public LogViewModel(ILogService logService)
    {
        _logService = logService;

        // Load existing entries
        foreach (var entry in _logService.Entries)
            Entries.Add(entry);

        // Subscribe to new entries
        _logService.EntryAdded += OnEntryAdded;
    }

    private void OnEntryAdded(LogEntry entry)
    {
        // Insert at position 0 (newest first)
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Entries.Insert(0, entry);
        });
    }

    /// <summary>
    /// Clear the visible log entries.
    /// </summary>
    [RelayCommand]
    public void Clear()
    {
        Entries.Clear();
        foreach (var entry in _logService.Entries)
            Entries.Add(entry);
    }
}
