namespace UsbFreeInstaller.Core.Abstractions;

public enum SafeLogLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Command = 3
}

public sealed record SafeLogEntry(
    DateTimeOffset TimestampUtc,
    SafeLogLevel Level,
    string Category,
    string Message);

public interface ISafeLogger
{
    string? LogFilePath { get; }

    event EventHandler<SafeLogEntry>? EntryWritten;

    IReadOnlyList<SafeLogEntry> GetEntriesSnapshot();

    void Log(SafeLogLevel level, string category, string message);

    void Info(string category, string message) => Log(SafeLogLevel.Info, category, message);

    void Warn(string category, string message) => Log(SafeLogLevel.Warning, category, message);

    void Error(string category, string message) => Log(SafeLogLevel.Error, category, message);

    void Command(string category, string commandSummary) => Log(SafeLogLevel.Command, category, commandSummary);
}
