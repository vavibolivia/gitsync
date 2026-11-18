namespace GitSync.Core;

public enum SyncLogLevel { Info, Success, Warning, Error, Command }

public sealed record LogEntry(long Id, DateTimeOffset Time, SyncLogLevel Level, string Scope, string Message, string? Detail);

/// <summary>Thread-safe logger with a bounded in-memory history.</summary>
public sealed class SyncLogger
{
    private readonly object _gate = new();
    private readonly LinkedList<LogEntry> _entries = new();
    private long _nextId;
    public int Capacity { get; init; } = 3000;

    public event Action<LogEntry>? Logged;

    public LogEntry Write(SyncLogLevel level, string message, string scope = "", string? detail = null)
    {
        LogEntry entry;
        lock (_gate)
        {
            entry = new LogEntry(++_nextId, DateTimeOffset.Now, level, scope, message,
                string.IsNullOrWhiteSpace(detail) ? null : detail.Trim());
            _entries.AddLast(entry);
            while (_entries.Count > Capacity) _entries.RemoveFirst();
        }
        Logged?.Invoke(entry);
        return entry;
    }

    public void Info(string m, string scope = "", string? d = null) => Write(SyncLogLevel.Info, m, scope, d);
    public void Success(string m, string scope = "", string? d = null) => Write(SyncLogLevel.Success, m, scope, d);
    public void Warn(string m, string scope = "", string? d = null) => Write(SyncLogLevel.Warning, m, scope, d);
    public void Error(string m, string scope = "", string? d = null) => Write(SyncLogLevel.Error, m, scope, d);
    public void Command(string m, string scope = "", string? d = null) => Write(SyncLogLevel.Command, m, scope, d);

    public IReadOnlyList<LogEntry> Since(long afterId, int max = 500)
    {
        lock (_gate) return _entries.Where(e => e.Id > afterId).TakeLast(max).ToList();
    }

    public void Clear() { lock (_gate) _entries.Clear(); }
}
