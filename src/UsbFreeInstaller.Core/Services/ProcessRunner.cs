using System.Diagnostics;
using System.Text;
using UsbFreeInstaller.Core.Abstractions;

namespace UsbFreeInstaller.Core.Services;

public sealed class ProcessRunner : IProcessRunner
{
    private const int SafeInlineEncodedCommandMaxChars = 8_000;
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly ISafeLogger _logger;

    public ProcessRunner(ISafeLogger logger)
    {
        _logger = logger;
    }

    public static ProcessRunRequest CreatePowerShellRequest(
        string script,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        TimeSpan? timeout = null)
    {
        string wrappedScript =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
            "$ProgressPreference = 'SilentlyContinue'\n" +
            script;

        byte[] utf16Bytes = Encoding.Unicode.GetBytes(wrappedScript);
        string encodedCommand = Convert.ToBase64String(utf16Bytes);

        return new ProcessRunRequest
        {
            FileName = "powershell.exe",
            Arguments = new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-EncodedCommand",
                encodedCommand
            },
            EnvironmentVariables = environmentVariables,
            Timeout = timeout ?? TimeSpan.FromMinutes(15)
        };
    }

    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string? tempPs1File = null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            IReadOnlyList<string> effectiveArgs = PrepareArguments(request, out tempPs1File);
            foreach (string arg in effectiveArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

            if (request.EnvironmentVariables is not null)
            {
                foreach ((string key, string value) in request.EnvironmentVariables)
                {
                    startInfo.Environment[key] = value;
                }
            }

            string displayArgs = string.Equals(request.FileName, "powershell.exe", StringComparison.OrdinalIgnoreCase)
                ? (tempPs1File is not null ? "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File <temp-script.ps1>" : "-NoProfile -NonInteractive -EncodedCommand <script>")
                : string.Join(' ', effectiveArgs);
            _logger.Command("Process", $"{request.FileName} {displayArgs}");

            using var process = new Process { StartInfo = startInfo };

            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start external process '{request.FileName}'.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(request.Timeout);

            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stdErrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryTerminateProcessTree(process);
                throw;
            }

            string stdOut = await stdOutTask.ConfigureAwait(false);
            string rawStdErr = await stdErrTask.ConfigureAwait(false);
            string stdErr = PowerShellCliXmlCleaner.Clean(rawStdErr);
            stopwatch.Stop();

            if (process.ExitCode != 0)
            {
                _logger.Warn(
                    "Process",
                    $"Process '{request.FileName}' exited with code {process.ExitCode}. StdErr: {stdErr.Trim()}");
            }

            return new ProcessRunResult(process.ExitCode, stdOut, stdErr, stopwatch.Elapsed);
        }
        finally
        {
            SwbiWorkDirectory.TryDeleteFile(tempPs1File);
        }
    }

    private static IReadOnlyList<string> PrepareArguments(ProcessRunRequest request, out string? tempPs1File)
    {
        tempPs1File = null;

        // Windows CreateProcessW enforces a hard 32,767 UTF-16 character command-line limit.
        // Spill large PowerShell -EncodedCommand payloads into a temporary UTF-8-BOM .ps1 script automatically.
        if (string.Equals(request.FileName, "powershell.exe", StringComparison.OrdinalIgnoreCase) &&
            request.Arguments.Count >= 2 &&
            string.Equals(request.Arguments[^2], "-EncodedCommand", StringComparison.OrdinalIgnoreCase) &&
            request.Arguments[^1].Length > SafeInlineEncodedCommandMaxChars)
        {
            byte[] scriptUtf16 = Convert.FromBase64String(request.Arguments[^1]);
            string scriptText = Encoding.Unicode.GetString(scriptUtf16);

            string safeWorkDir = SwbiWorkDirectory.EnsureSafeWorkDirectory("scripts");
            tempPs1File = Path.Combine(safeWorkDir, $"swbi-ps-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(tempPs1File, scriptText, Utf8WithBom);

            var rewritten = new List<string>(request.Arguments.Count);
            for (int i = 0; i < request.Arguments.Count - 2; i++)
            {
                rewritten.Add(request.Arguments[i]);
            }

            rewritten.Add("-File");
            rewritten.Add(tempPs1File);
            return rewritten;
        }

        return request.Arguments;
    }

    private static void TryTerminateProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore cleanup errors on already-exited child processes
        }
    }
}
