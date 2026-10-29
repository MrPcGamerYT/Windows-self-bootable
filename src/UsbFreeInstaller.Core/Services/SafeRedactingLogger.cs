using System.Text;
using System.Text.RegularExpressions;
using UsbFreeInstaller.Core.Abstractions;

namespace UsbFreeInstaller.Core.Services;

/// <summary>
/// Thread-safe structured logger that automatically redacts 48-digit BitLocker recovery passwords
/// and 25-character Windows product keys before storing entries in memory or writing to disk.
/// </summary>
public sealed partial class SafeRedactingLogger : ISafeLogger
{
    // Matches 8 groups of 6 digits separated by hyphens or spaces (BitLocker 48-digit numerical recovery password)
    [GeneratedRegex(@"\b(?:\d{6}[-\s]){7}\d{6}\b", RegexOptions.CultureInvariant)]
    private static partial Regex BitLockerRecoveryKeyRegex();

    // Matches 5 groups of 5 alphanumeric product-key characters
    [GeneratedRegex(@"\b(?:[A-Z0-9]{5}-){4}[A-Z0-9]{5}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsProductKeyRegex();

    private readonly object _sync = new();
    private readonly List<SafeLogEntry> _entries = new();

    public string? LogFilePath { get; }

    public event EventHandler<SafeLogEntry>? EntryWritten;

    public SafeRedactingLogger(string? logDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                string fileName = $"swbi-session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log";
                LogFilePath = Path.Combine(logDirectory, fileName);
            }
            catch
            {
                // Fall back to in-memory logging if the directory is not writable
                LogFilePath = null;
            }
        }
    }

    public static string RedactSecrets(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        string sanitized = BitLockerRecoveryKeyRegex().Replace(input, "[REDACTED-BITLOCKER-RECOVERY-KEY]");
        sanitized = WindowsProductKeyRegex().Replace(sanitized, "[REDACTED-PRODUCT-KEY]");
        return sanitized;
    }

    public IReadOnlyList<SafeLogEntry> GetEntriesSnapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }

    public void Log(SafeLogLevel level, string category, string message)
    {
        string safeCategory = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
        string safeMessage = RedactSecrets(message);
        var entry = new SafeLogEntry(DateTimeOffset.UtcNow, level, safeCategory, safeMessage);

        lock (_sync)
        {
            _entries.Add(entry);

            if (!string.IsNullOrWhiteSpace(LogFilePath))
            {
                try
                {
                    string line =
                        $"{entry.TimestampUtc:yyyy-MM-dd HH:mm:ss.fff}Z [{entry.Level.ToString().ToUpperInvariant()}] [{entry.Category}] {entry.Message}{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, line, Encoding.UTF8);
                }
                catch
                {
                    // Never crash critical disk/BCD workflows due to log I/O contention
                }
            }
        }

        // UI/event subscribers must not interrupt a disk or boot operation after it changed state.
        foreach (EventHandler<SafeLogEntry> handler in EntryWritten?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { handler(this, entry); }
            catch { /* Logging observers cannot compromise the operation being logged. */ }
        }
    }
}
