using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services.Native;

namespace UsbFreeInstaller.Core.Services;

/// <summary>
/// Collects evidence for the SWBI boot repair assistant. It never changes the BCD,
/// firmware variables, boot order, or partitions; it cannot authorize cleanup.
/// </summary>
public interface IReadOnlyBootRepairDiagnostics
{
    Task<string> CaptureAsync(CleanupInspectionResult inspection, CancellationToken cancellationToken = default);
}

public sealed class WindowsReadOnlyBootRepairDiagnostics : IReadOnlyBootRepairDiagnostics
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private const int MaxOutputCharacters = 64 * 1024;

    internal sealed record BootQuery(string Label, string Identifier)
    {
        internal string[] Arguments => ["/enum", Identifier, "/v"];
    }

    /// <summary>Only fixed /enum commands with GUID-normalized identifiers are permitted.</summary>
    internal static IReadOnlyList<BootQuery> BuildQueries(CleanupInspectionResult inspection)
    {
        var queries = new List<BootQuery>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddGuid(string? id, string label)
        {
            if (Guid.TryParse(id, out Guid parsed) && parsed != Guid.Empty)
            {
                string normalized = $"{{{parsed:D}}}";
                if (seen.Add(normalized)) queries.Add(new BootQuery(label, normalized));
            }
        }

        AddGuid(inspection.OrphanedBcdFirmwareGuid, "BCD firmware-view object");
        AddGuid(inspection.OrphanedBcdLoaderGuid, "BCD WinPE loader");
        AddGuid(inspection.OrphanedBcdSdiGuid, "BCD SDI device");
        queries.Add(new BootQuery("UEFI firmware view", "firmware"));
        queries.Add(new BootQuery("Windows Boot Manager", "{bootmgr}"));
        return queries;
    }

    public async Task<string> CaptureAsync(CleanupInspectionResult inspection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        cancellationToken.ThrowIfCancellationRequested();

        var report = new StringBuilder();
        report.AppendLine("SWBI BOOT REPAIR ASSESSMENT — READ ONLY")
            .AppendLine($"Captured (UTC): {DateTimeOffset.UtcNow:O}")
            .AppendLine($"Cleanup status: {inspection.State}")
            .AppendLine($"Windows: Disk {inspection.CurrentOsDiskNumber}, Partition {inspection.CurrentOsPartitionNumber}")
            .AppendLine($"Temporary installer partition: {(inspection.CandidatePartition is null ? "not detected" : $"Disk {inspection.CandidatePartition.DiskNumber} Partition {inspection.CandidatePartition.PartitionNumber} ({inspection.CandidatePartition.Label})")}")
            .AppendLine($"BCD loader: {inspection.OrphanedBcdLoaderGuid ?? "(none found)"}")
            .AppendLine($"BCD SDI: {inspection.OrphanedBcdSdiGuid ?? "(none found)"}")
            .AppendLine($"Firmware identifier: {inspection.OrphanedBcdFirmwareGuid ?? "(none found)"}")
            .AppendLine("No boot or disk settings were changed. A braced BCD firmware GUID is NOT proof of ownership of a UEFI BootXXXX slot.")
            .AppendLine("This report cannot authorize deletion. Remove only independently verified SWBI entries after a backup and explicit confirmation.")
            .AppendLine("If you share this report, review it for private device identifiers first.")
            .AppendLine();

        foreach (SystemCheckItem check in inspection.SafetyChecks)
            report.AppendLine($"[{check.Severity}] {check.Title}: {check.Summary}");

        if (!OperatingSystem.IsWindows())
        {
            report.AppendLine("\nBCD/UEFI enumeration is available on an elevated Windows desktop only.");
            return report.ToString();
        }

        string? executable = FindSystemBcdEdit();
        if (executable is null)
        {
            report.AppendLine("\nThe system bcdedit.exe was unavailable. Run read-only 'bcdedit /enum firmware /v' and 'bcdedit /enum {bootmgr} /v' from an Administrator Command Prompt instead.");
        }
        else
        {
            foreach (BootQuery query in BuildQueries(inspection))
            {
                cancellationToken.ThrowIfCancellationRequested();
                report.AppendLine().AppendLine($"--- READ ONLY: bcdedit /enum {query.Identifier} /v ({query.Label}) ---");
                try
                {
                    report.AppendLine(await RunQueryAsync(executable, query, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    report.AppendLine("The read-only query timed out; no boot setting was changed. Inspect manually in an elevated Command Prompt.");
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
                {
                    report.AppendLine($"Read-only query unavailable ({ex.GetType().Name}: {ex.Message}). No boot setting was changed.");
                }
            }
        }

        report.AppendLine().AppendLine("--- READ ONLY: raw EFI_GLOBAL BootXXXX variables (not BCD GUIDs) ---");
        try
        {
            // Firmware calls are synchronous; keep a slow or unavailable firmware out
            // of the WPF UI thread, especially when bcdedit.exe was unavailable.
            string rawReport = await Task.Run(() =>
            {
                if (!Win32StorageInterop.EnableProcessPrivilege("SeSystemEnvironmentPrivilege"))
                    return "UEFI firmware-variable privilege unavailable. No raw BootXXXX conclusions can be drawn; nothing was changed.";
                return RawUefiBootDiagnostics.Capture(new WindowsFirmwareVariableStore(), cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
            report.AppendLine(rawReport);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            report.AppendLine($"Raw firmware-variable read unavailable ({ex.GetType().Name}: {ex.Message}). Do not infer that any SWBI-named BootXXXX entry is absent.");
        }

        return report.ToString();
    }

    private static string? FindSystemBcdEdit()
    {
        // Sysnative bypasses WoW64 redirection for an x86 SWBI process on 64-bit Windows.
        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
        {
            string sysnative = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "bcdedit.exe");
            if (File.Exists(sysnative)) return sysnative;
        }

        string system = Path.Combine(Environment.SystemDirectory, "bcdedit.exe");
        return File.Exists(system) ? system : null; // Never run an executable from an untrusted PATH.
    }

    private static async Task<string> RunQueryAsync(string executable, BootQuery query, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (string arg in query.Arguments) process.StartInfo.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QueryTimeout);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("bcdedit.exe could not start.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string text = await stdout.ConfigureAwait(false);
            string errorText = await stderr.ConfigureAwait(false);
            string combined = text + (string.IsNullOrWhiteSpace(errorText) ? string.Empty : "\n" + errorText);
            if (combined.Length > MaxOutputCharacters)
                combined = combined[..MaxOutputCharacters] + "\n[Output truncated; inspect this entry directly in an elevated Command Prompt.]";
            return $"Exit code: {process.ExitCode}\n{(string.IsNullOrWhiteSpace(combined) ? "(no output returned; do not infer that the entry is absent)" : combined.TrimEnd())}";
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Already exited. */ }
            catch (Win32Exception) { /* Report timeout; never issue any other command. */ }
            throw;
        }
    }
}
