using Microsoft.Data.Sqlite;
using Phobri.Desktop.Models;

namespace Phobri.Desktop.Services;

/// <summary>
/// Provides local SQLite storage for SMS messages and call logs.
/// Enables offline access and search.
/// Uses SQLite3 Multiple Ciphers for page-level AES encryption.
/// The database file is ALWAYS encrypted on disk — no plaintext ever
/// touches the filesystem. No temp files, no decrypt-to-disk.
/// </summary>
public interface IDataService
{
    /// <summary>Whether the database is currently open and unlocked.</summary>
    bool IsOpen { get; }

    /// <summary>No-op (kept for API compatibility).</summary>
    Task InitializeAsync();

    /// <summary>
    /// Open the encrypted database with the given DEK.
    /// The DEK is used directly as the SQLite encryption key.
    /// Creates the database and schema if first run.
    /// </summary>
    Task UnlockAsync(byte[] dek);

    /// <summary>
    /// Lock the database: close the connection.
    /// The file remains encrypted on disk (always was).
    /// </summary>
    Task LockAsync();

    // SMS operations
    Task UpsertSmsAsync(SmsMessage message);
    Task UpsertSmsBatchAsync(IEnumerable<SmsMessage> messages);
    Task<List<SmsMessage>> GetSmsMessagesAsync(long? after = null, int limit = 100);
    Task<List<SmsMessage>> GetConversationAsync(string address, int limit = 100);

    // Call log operations
    Task UpsertCallAsync(CallLogEntry entry);
    Task UpsertCallBatchAsync(IEnumerable<CallLogEntry> entries);
    Task<List<CallLogEntry>> GetCallLogAsync(long? after = null, int limit = 100);

    // Sync state
    Task<long> GetLastSyncTimestampAsync(string dataType);
    Task SetLastSyncTimestampAsync(string dataType, long timestamp);
}

public sealed class DataService : IDataService, IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private byte[]? _dek;
    private bool _isOpen;

    public DataService(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        _dbPath = dbPath;
    }

    /// <inheritdoc/>
    public bool IsOpen => _isOpen;

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task UnlockAsync(byte[] dek)
    {
        if (_isOpen)
            return;

        _dek = dek;

        // Build connection string with encryption key.
        // DEK (32 random bytes) is hex-encoded and used as the SQLite key.
        // SQLite3MC applies PBKDF2 internally; the DEK's 256 bits of
        // entropy ensure strong encryption regardless.
        var dekHex = Convert.ToHexStringLower(dek);
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Password = dekHex,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };

        _connection = new SqliteConnection(csb.ToString());
        await _connection.OpenAsync();

        // PRAGMA settings: use DELETE journal mode (no WAL files
        // to worry about; the journal is also encrypted by SQLite3MC).
        using var pragmaCmd = _connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;";
        await pragmaCmd.ExecuteNonQueryAsync();

        // Ensure schema is up to date
        await EnsureSchemaAsync();

        _isOpen = true;
    }

    /// <inheritdoc/>
    public async Task LockAsync()
    {
        if (!_isOpen || _connection is null)
            return;

        _isOpen = false;

        var conn = _connection;
        _connection = null;
        _dek = null;

        try
        {
            await conn.CloseAsync();
        }
        catch { }
        conn.Dispose();
    }

    // --- SMS Operations ---

    public async Task UpsertSmsAsync(SmsMessage message)
    {
        await EnsureOpenAsync();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO sms_messages (id, thread_id, address, contact_name, body, date, type, read)
            VALUES (@id, @threadId, @address, @contactName, @body, @date, @type, @read)
            ON CONFLICT(id) DO UPDATE SET
                thread_id = excluded.thread_id,
                contact_name = excluded.contact_name,
                body = excluded.body,
                type = excluded.type,
                read = excluded.read,
                synced_at = (strftime('%s','now') * 1000);
        ";
        AddSmsParams(cmd, message);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpsertSmsBatchAsync(IEnumerable<SmsMessage> messages)
    {
        await EnsureOpenAsync();

        using var transaction = _connection!.BeginTransaction();
        foreach (var msg in messages)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO sms_messages (id, thread_id, address, contact_name, body, date, type, read)
                VALUES (@id, @threadId, @address, @contactName, @body, @date, @type, @read)
                ON CONFLICT(id) DO UPDATE SET
                    thread_id = excluded.thread_id,
                    contact_name = excluded.contact_name,
                    body = excluded.body,
                    type = excluded.type,
                    read = excluded.read,
                    synced_at = (strftime('%s','now') * 1000);
            ";
            AddSmsParams(cmd, msg);
            await cmd.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    public async Task<List<SmsMessage>> GetSmsMessagesAsync(long? after = null, int limit = 100)
    {
        await EnsureOpenAsync();

        using var cmd = _connection!.CreateCommand();
        if (after.HasValue)
        {
            cmd.CommandText = "SELECT * FROM sms_messages WHERE date > @after ORDER BY date DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@after", after.Value);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM sms_messages ORDER BY date DESC LIMIT @limit";
        }
        cmd.Parameters.AddWithValue("@limit", limit);

        return await ReadSmsMessagesAsync(cmd);
    }

    public async Task<List<SmsMessage>> GetConversationAsync(string address, int limit = 100)
    {
        await EnsureOpenAsync();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM sms_messages
            WHERE address = @address
            ORDER BY date DESC
            LIMIT @limit
        ";
        cmd.Parameters.AddWithValue("@address", address);
        cmd.Parameters.AddWithValue("@limit", limit);

        return await ReadSmsMessagesAsync(cmd);
    }

    // --- Call Log Operations ---

    public async Task UpsertCallAsync(CallLogEntry entry)
    {
        await EnsureOpenAsync();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO call_log (id, number, contact_name, date, duration, type)
            VALUES (@id, @number, @contactName, @date, @duration, @type)
            ON CONFLICT(id) DO UPDATE SET
                contact_name = excluded.contact_name,
                duration = excluded.duration,
                synced_at = (strftime('%s','now') * 1000);
        ";
        AddCallParams(cmd, entry);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpsertCallBatchAsync(IEnumerable<CallLogEntry> entries)
    {
        await EnsureOpenAsync();

        using var transaction = _connection!.BeginTransaction();
        foreach (var entry in entries)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO call_log (id, number, contact_name, date, duration, type)
                VALUES (@id, @number, @contactName, @date, @duration, @type)
                ON CONFLICT(id) DO UPDATE SET
                    contact_name = excluded.contact_name,
                    duration = excluded.duration,
                    synced_at = (strftime('%s','now') * 1000);
            ";
            AddCallParams(cmd, entry);
            await cmd.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    public async Task<List<CallLogEntry>> GetCallLogAsync(long? after = null, int limit = 100)
    {
        await EnsureOpenAsync();

        using var cmd = _connection!.CreateCommand();
        if (after.HasValue)
        {
            cmd.CommandText = "SELECT * FROM call_log WHERE date > @after ORDER BY date DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@after", after.Value);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM call_log ORDER BY date DESC LIMIT @limit";
        }
        cmd.Parameters.AddWithValue("@limit", limit);

        return await ReadCallLogAsync(cmd);
    }

    // --- Sync State ---

    public async Task<long> GetLastSyncTimestampAsync(string dataType)
    {
        await EnsureOpenAsync();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT last_sync_timestamp FROM sync_state WHERE data_type = @type";
        cmd.Parameters.AddWithValue("@type", dataType);

        var result = await cmd.ExecuteScalarAsync();
        return result is long ts ? ts : 0;
    }

    public async Task SetLastSyncTimestampAsync(string dataType, long timestamp)
    {
        await EnsureOpenAsync();

        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO sync_state (data_type, last_sync_timestamp)
            VALUES (@type, @ts)
            ON CONFLICT(data_type) DO UPDATE SET last_sync_timestamp = excluded.last_sync_timestamp;
        ";
        cmd.Parameters.AddWithValue("@type", dataType);
        cmd.Parameters.AddWithValue("@ts", timestamp);
        await cmd.ExecuteNonQueryAsync();
    }

    // --- Private Helpers ---

    private async Task EnsureOpenAsync()
    {
        if (!_isOpen || _connection is null)
            throw new InvalidOperationException("Database is locked. Call UnlockAsync first.");
        await Task.CompletedTask;
    }

    private async Task EnsureSchemaAsync()
    {
        if (_connection is null) return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS sms_messages (
                id INTEGER PRIMARY KEY,
                thread_id INTEGER NOT NULL DEFAULT 0,
                address TEXT NOT NULL,
                contact_name TEXT,
                body TEXT NOT NULL DEFAULT '',
                date INTEGER NOT NULL,
                type TEXT NOT NULL DEFAULT 'inbox',
                read INTEGER NOT NULL DEFAULT 1,
                synced_at INTEGER NOT NULL DEFAULT (strftime('%s','now') * 1000)
            );

            CREATE INDEX IF NOT EXISTS idx_sms_date ON sms_messages(date DESC);
            CREATE INDEX IF NOT EXISTS idx_sms_address ON sms_messages(address);
            CREATE INDEX IF NOT EXISTS idx_sms_thread ON sms_messages(thread_id);

            CREATE TABLE IF NOT EXISTS call_log (
                id INTEGER PRIMARY KEY,
                number TEXT NOT NULL,
                contact_name TEXT,
                date INTEGER NOT NULL,
                duration INTEGER NOT NULL DEFAULT 0,
                type TEXT NOT NULL DEFAULT 'incoming',
                synced_at INTEGER NOT NULL DEFAULT (strftime('%s','now') * 1000)
            );

            CREATE INDEX IF NOT EXISTS idx_call_date ON call_log(date DESC);
            CREATE INDEX IF NOT EXISTS idx_call_number ON call_log(number);

            CREATE TABLE IF NOT EXISTS sync_state (
                data_type TEXT PRIMARY KEY,
                last_sync_timestamp INTEGER NOT NULL DEFAULT 0
            );
        ";
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddSmsParams(SqliteCommand cmd, SmsMessage msg)
    {
        cmd.Parameters.AddWithValue("@id", msg.Id);
        cmd.Parameters.AddWithValue("@threadId", msg.ThreadId);
        cmd.Parameters.AddWithValue("@address", msg.Address);
        cmd.Parameters.AddWithValue("@contactName", msg.ContactName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@body", msg.Body);
        cmd.Parameters.AddWithValue("@date", msg.Date);
        cmd.Parameters.AddWithValue("@type", msg.Type.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@read", msg.Read ? 1 : 0);
    }

    private static void AddCallParams(SqliteCommand cmd, CallLogEntry entry)
    {
        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@number", entry.Number);
        cmd.Parameters.AddWithValue("@contactName", entry.ContactName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@date", entry.Date);
        cmd.Parameters.AddWithValue("@duration", entry.Duration);
        cmd.Parameters.AddWithValue("@type", entry.Type.ToString().ToLowerInvariant());
    }

    private static async Task<List<SmsMessage>> ReadSmsMessagesAsync(SqliteCommand cmd)
    {
        var messages = new List<SmsMessage>();
        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            messages.Add(new SmsMessage
            {
                Id = reader.GetInt64(0),
                ThreadId = reader.GetInt64(1),
                Address = reader.GetString(2),
                ContactName = reader.IsDBNull(3) ? null : reader.GetString(3),
                Body = reader.GetString(4),
                Date = reader.GetInt64(5),
                Type = Enum.Parse<SmsType>(reader.GetString(6), ignoreCase: true),
                Read = reader.GetInt32(7) != 0
            });
        }
        return messages;
    }

    private static async Task<List<CallLogEntry>> ReadCallLogAsync(SqliteCommand cmd)
    {
        var calls = new List<CallLogEntry>();
        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            calls.Add(new CallLogEntry
            {
                Id = reader.GetInt64(0),
                Number = reader.GetString(1),
                ContactName = reader.IsDBNull(2) ? null : reader.GetString(2),
                Date = reader.GetInt64(3),
                Duration = reader.GetInt64(4),
                Type = Enum.Parse<CallType>(reader.GetString(5), ignoreCase: true)
            });
        }
        return calls;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
        _dek = null;
    }
}
