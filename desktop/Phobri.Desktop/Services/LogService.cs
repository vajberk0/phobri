using System.Collections.Concurrent;

namespace Phobri.Desktop.Services;

/// <summary>
/// A structured log event emitted by desktop components.
/// </summary>
public sealed record LogEntry(
    DateTime Timestamp,
    string Category,
    string Message,
    string? Detail = null);

/// <summary>
/// Central event log for the desktop app. Collects log entries from
/// WebSocket handler, sync server, REST endpoints, and other components.
/// The UI can bind to this to display a real-time log viewer.
/// </summary>
public interface ILogService
{
    /// <summary>Log an event.</summary>
    void Log(LogEntry entry);

    /// <summary>Convenience: log with category + message.</summary>
    void Log(string category, string message, string? detail = null);

    /// <summary>All log entries (most recent first).</summary>
    IReadOnlyList<LogEntry> Entries { get; }

    /// <summary>Raised when a new entry is added.</summary>
    event Action<LogEntry>? EntryAdded;
}

public sealed class LogService : ILogService
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private const int MaxEntries = 1000;

    /// <inheritdoc/>
    public event Action<LogEntry>? EntryAdded;

    /// <inheritdoc/>
    public IReadOnlyList<LogEntry> Entries => _entries.Reverse().ToList();

    /// <inheritdoc/>
    public void Log(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
        EntryAdded?.Invoke(entry);
    }

    /// <inheritdoc/>
    public void Log(string category, string message, string? detail = null)
    {
        Log(new LogEntry(DateTime.Now, category, message, detail));
    }
}
