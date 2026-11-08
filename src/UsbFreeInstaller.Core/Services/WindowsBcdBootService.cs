using System.ComponentModel;
using System.Text.RegularExpressions;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services.Native;

namespace UsbFreeInstaller.Core.Services;

/// <summary>
/// 100% Self-Contained Native UEFI NVRAM (SetFirmwareEnvironmentVariableExW) + Registry BCD Hive
/// (HKLM\BCD00000000 + RegSaveKeyExW) + Native System Reboot (InitiateSystemShutdownExW) Boot Service.
/// Operates with ZERO reliance on bcdedit.exe, shutdown.exe, or powershell.exe.
/// </summary>
public sealed partial class WindowsBcdBootService : IBcdBootService
{
    [GeneratedRegex(@"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}", RegexOptions.CultureInvariant)]
    private static partial Regex BcdGuidRegex();

    private readonly ISafeLogger _logger;
    private readonly IBootProvisioningBackend _backend;

    public WindowsBcdBootService(IProcessRunner processRunner, ISafeLogger logger)
        : this(logger, new NativeBootProvisioningBackend())
    {
    }

    internal WindowsBcdBootService(ISafeLogger logger, IBootProvisioningBackend backend)
    {
        _logger = logger;
        _backend = backend;
    }

    public Task<BcdProvisionResult> ConfigureOneTimeWinPeBootAsync(
        string installerDriveLetter,
        string backupDirectory,
        FirmwareMode firmwareMode = FirmwareMode.Uefi,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => ConfigureNativeBootCore(installerDriveLetter, backupDirectory, firmwareMode),
            cancellationToken);
    }

    private BcdProvisionResult ConfigureNativeBootCore(
        string installerDriveLetter,
        string backupDirectory,
        FirmwareMode firmwareMode)
    {
        string cleanLetter = installerDriveLetter.TrimEnd(':', '\\', '/').ToUpperInvariant();
        if (firmwareMode is not (FirmwareMode.Uefi or FirmwareMode.LegacyBios))
        {
            throw new InvalidOperationException("Firmware mode is unknown; refusing to choose a boot mechanism.");
        }

        // A BootNext entry pointing to an EFI file on a GPT Basic Data partition is not reliably
        // bootable on real firmware: many machines only load EFI programs from an ESP. Use the
        // *existing* Windows Boot Manager on both UEFI and BIOS, with a one-time BCD ramdisk
        // sequence. We never replace the host ESP boot manager or modify the ISO's local BCD.

        string safeBackupDir = !string.IsNullOrWhiteSpace(backupDirectory)
            ? backupDirectory
            : SwbiWorkDirectory.EnsureSafeWorkDirectory("bcd-backups");
        Directory.CreateDirectory(safeBackupDir);
        string backupPath = Path.Combine(safeBackupDir,
            $"swbi-bcd-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bcd");

        if (!_backend.BackupSystemBcd(backupPath, _logger))
        {
            throw new InvalidOperationException("Unable to back up the active system BCD; refusing to change the boot configuration.");
        }

        // Keep this guard even though the orchestrator performs the same inspection before
        // shrinking: a concurrent change between preflight and registration must still fail.
        string? blocker = GetPreparationBlocker(_backend.InspectSystemBcd());
        if (blocker is not null)
            throw new InvalidOperationException(blocker);

        (string sdiGuid, string loaderGuid) = _backend.RegisterRamdiskBcd(cleanLetter, firmwareMode, _logger);
        // Return the identifiers before any further work that could throw.
        return new BcdProvisionResult(backupPath, sdiGuid, loaderGuid, null);
    }

    /// <summary>Describes a read-only preflight failure; never infers that an old entry is safe to remove.</summary>
    public static string? GetPreparationBlocker(ActiveBcdState state)
    {
        if (!state.InspectionSucceeded)
            return "Cannot inspect the active system BCD; refusing to register a boot entry. Run as Administrator and inspect the SWBI log before retrying.";

        if (state.CurrentBootIsRamdisk)
            return "The current Windows session is booted from a ramdisk. Boot into the installed Windows desktop independently before preparing an installer.";

        string[] existing = new[]
        {
            state.ExistingUsbFreeLoaderGuid is { } loader ? $"BCD loader {loader}" : null,
            state.ExistingUsbFreeSdiGuid is { } sdi ? $"BCD SDI {sdi}" : null,
            state.ExistingFirmwareBootGuid is { } firmware ? $"UEFI/firmware entry {firmware}" : null
        }.Where(s => s is not null).Select(s => s!).ToArray();
        if (existing.Length > 0)
            return "Existing SWBI boot entries (" + string.Join(", ", existing) + ") must be inspected before preparing another boot. " +
                (state.FirmwareEntryVerified ? string.Empty
                    : "The named UEFI entry has an unverified EFI device path or unsupported slot and cannot be removed automatically. ") +
                "Do not retry preparation or delete boot entries manually. In '2. Post-Install Cleanup', click Inspect; " +
                "if there is no installer partition, the app can remove only verified SWBI boot entries after your confirmation.";

        if (state.HasPendingBootSequence)
            return "Another one-time Windows Boot Manager sequence is already pending. SWBI will not replace it. " +
                "Inspect the active BCD with an administrator before preparing; do not clear an unrelated boot request blindly.";

        return null;
    }

    public Task RollbackBcdEntriesAsync(
        string? osLoaderGuid,
        string? sdiDeviceGuid,
        CancellationToken cancellationToken = default,
        string? firmwareBootGuid = null)
    {
        if (osLoaderGuid is null && sdiDeviceGuid is null && firmwareBootGuid is null)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            // Cleanup needs its own backup: a previous preparation backup may not contain
            // intervening Windows/firmware changes. Fail closed if it cannot be written.
            string backupDir = SwbiWorkDirectory.EnsureSafeWorkDirectory("bcd-backups");
            string backup = Path.Combine(backupDir,
                $"swbi-pre-cleanup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bcd");
            if (!_backend.BackupSystemBcd(backup, _logger))
                throw new InvalidOperationException("Cannot back up the active BCD before SWBI boot-entry cleanup; nothing was removed.");

            _logger.Info("NativeBCD", $"Saved pre-cleanup active BCD backup at '{backup}'. Verifying ownership before removing recorded entries.");
            NativeUefiAndBcdRegistryEngine.RollbackRegistryAndUefiEntries(
                osLoaderGuid, sdiDeviceGuid, firmwareBootGuid, _logger);
        }, cancellationToken);
    }

    public Task<ActiveBcdState> InspectActiveBcdAsync(CancellationToken cancellationToken = default) =>
        Task.Run(_backend.InspectSystemBcd, cancellationToken);

    public Task RebootSystemAsync(
        int delaySeconds = 5,
        CancellationToken cancellationToken = default)
    {
        int clampedDelay = Math.Clamp(delaySeconds, 2, 60);
        string msg = $"Restarting into {SwbiBrand.BootChooserEntryName}...";
        _logger.Info("Reboot", $"Initiating native Win32 restart into '{SwbiBrand.BootChooserEntryName}' in {clampedDelay} seconds...");

        if (!NativeUefiAndBcdRegistryEngine.TryNativeSystemReboot(clampedDelay, msg, out int win32Err))
        {
            throw new Win32Exception(win32Err, $"Native InitiateSystemShutdownExW failed (Win32 error {win32Err}).");
        }

        return Task.CompletedTask;
    }

    public static (string? LoaderGuid, string? SdiGuid) FindUsbFreeBcdGuids(string bcdEnumAllOutput)
    {
        (string? loader, string? sdi, _) = FindSwbiBcdEntries(bcdEnumAllOutput);
        return (loader, sdi);
    }

    public static (string? LoaderGuid, string? SdiGuid, string? FirmwareBootGuid) FindSwbiBcdEntries(string bcdEnumAllOutput)
    {
        if (string.IsNullOrWhiteSpace(bcdEnumAllOutput))
        {
            return (null, null, null);
        }

        string? loaderGuid = null;
        string? sdiGuid = null;
        string? firmwareGuid = null;

        string[] blocks = Regex.Split(bcdEnumAllOutput, @"\r?\n\s*\r?\n");
        foreach (string block in blocks)
        {
            string idLine = ExtractBcdField(block, "identifier");
            string descLine = ExtractBcdField(block, "description");
            if (string.IsNullOrWhiteSpace(idLine) || string.IsNullOrWhiteSpace(descLine))
            {
                continue;
            }

            Match guidMatch = BcdGuidRegex().Match(idLine);
            if (!guidMatch.Success)
            {
                continue;
            }

            if (SwbiBrand.IsRecognizedBootDescription(descLine))
            {
                if (block.Contains(@"\efi\boot\", StringComparison.OrdinalIgnoreCase) &&
                    !block.Contains("ramdisk=", StringComparison.OrdinalIgnoreCase))
                {
                    firmwareGuid = guidMatch.Value;
                }
                else
                {
                    loaderGuid = guidMatch.Value;
                }
            }
            else if (SwbiBrand.IsRecognizedSdiDescription(descLine))
            {
                sdiGuid = guidMatch.Value;
            }
        }

        return (loaderGuid, sdiGuid, firmwareGuid);
    }

    private static string ExtractBcdField(string text, string fieldName)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith(fieldName, StringComparison.OrdinalIgnoreCase))
            {
                string remainder = line[fieldName.Length..].TrimStart();
                return remainder;
            }
        }

        return string.Empty;
    }
}
