namespace UsbFreeInstaller.Core.Abstractions;

public sealed record ProcessRunRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);
}

public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default);
}
