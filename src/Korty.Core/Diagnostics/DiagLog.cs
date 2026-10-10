using System.Text;
using System.Text.RegularExpressions;

namespace Korty.Core.Diagnostics;

public enum LogLevel { Info, Warn, Error, Gate }

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff} [{Level,-5}] {Category}: {Message}";
}

/// <summary>
/// In-memory ring buffer + rolling file sink. Local only — nothing is ever transmitted. HID device paths and any
/// long hex tokens (instance ids / serial-like strings) are redacted before they reach memory or disk.
/// </summary>
public sealed partial class DiagLog
{
    private readonly object _lock = new();
    private readonly Queue<LogEntry> _ring = new();
    private readonly string? _directory;
    private const int RingSize = 2000;
    private const long MaxFileBytes = 1_000_000;
    private const int KeepFiles = 5;

    public event Action<LogEntry>? Appended;

    public DiagLog(string? directory = null)
    {
        _directory = directory;
        if (directory != null)
        {
            try { Directory.CreateDirectory(directory); Rotate(); } catch { _directory = null; }
        }
    }

    public IReadOnlyList<LogEntry> Snapshot() { lock (_lock) return _ring.ToList(); }

    public void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public void Warn(string category, string message) => Write(LogLevel.Warn, category, message);
    public void Error(string category, string message) => Write(LogLevel.Error, category, message);
    public void Gate(string message) => Write(LogLevel.Gate, "gate", message);

    private void Write(LogLevel level, string category, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, Redact(message));
        lock (_lock)
        {
            _ring.Enqueue(entry);
            while (_ring.Count > RingSize) _ring.Dequeue();
            if (_directory != null)
            {
                try
                {
                    var file = Path.Combine(_directory, "studio.log");
                    if (File.Exists(file) && new FileInfo(file).Length > MaxFileBytes) Rotate(force: true);
                    File.AppendAllText(file, entry + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* logging must never throw */ }
            }
        }
        try { Appended?.Invoke(entry); } catch { /* subscriber bug must not break logging */ }
    }

    private void Rotate(bool force = false)
    {
        if (_directory == null) return;
        var file = Path.Combine(_directory, "studio.log");
        if (force && File.Exists(file))
        {
            var archive = Path.Combine(_directory, $"studio-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.Move(file, archive, overwrite: true);
        }
        var old = new DirectoryInfo(_directory).GetFiles("studio-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepFiles);
        foreach (var f in old) try { f.Delete(); } catch { /* ignore */ }
    }

    [GeneratedRegex(@"\\\\\?\\hid#[^\s""]+", RegexOptions.IgnoreCase)] private static partial Regex HidPath();
    [GeneratedRegex(@"\b[0-9A-Fa-f]{16,}\b")] private static partial Regex LongHex();
    [GeneratedRegex(@"C:\\Users\\[^\\\s""]+", RegexOptions.IgnoreCase)] private static partial Regex UserProfile();

    public static string Redact(string text)
    {
        text = HidPath().Replace(text, "<hid-path>");
        text = LongHex().Replace(text, "<hex>");
        text = UserProfile().Replace(text, @"C:\Users\<user>");
        return text;
    }

    public string Export() => string.Join(Environment.NewLine, Snapshot().Select(e => e.ToString()));
}
