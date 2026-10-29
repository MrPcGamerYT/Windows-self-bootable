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
        if (firmwareMode == FirmwareMode.Uefi)
        {
            // A UEFI run never modifies either BCD store or the host's bootmgfw.efi.
            string firmwareId = _backend.RegisterUefi(cleanLetter, _logger);
            return new BcdProvisionResult(null, null, null, firmwareId);
        }

        if (firmwareMode != FirmwareMode.LegacyBios)
        {
            throw new InvalidOperationException("Firmware mode is unknown; refusing to choose a boot mechanism.");
        }

        string safeBackupDir = !string.IsNullOrWhiteSpace(backupDirectory)
            ? backupDirectory
            : SwbiWorkDirectory.EnsureSafeWorkDirectory("bcd-backups");
        Directory.CreateDirectory(safeBackupDir);
        string backupPath = Path.Combine(safeBackupDir,
            $"swbi-bcd-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bcd");

        if (!_backend.BackupSystemBcd(backupPath, _logger))
        {
            throw new InvalidOperationException("Unable to back up the system BCD; refusing to change the legacy boot configuration.");
        }

        ActiveBcdState priorState = _backend.InspectSystemBcd();
        if (priorState.ExistingUsbFreeLoaderGuid is not null || priorState.ExistingUsbFreeSdiGuid is not null)
        {
            throw new InvalidOperationException("Existing SWBI BCD entries must be inspected and cleaned up before preparing another legacy boot.");
        }

        (string sdiGuid, string loaderGuid) = _backend.RegisterLegacyBcd(cleanLetter, _logger);
        // Return the identifiers before any further work that could throw.
        return new BcdProvisionResult(backupPath, sdiGuid, loaderGuid, null);
    }

    public Task RollbackBcdEntriesAsync(
        string? osLoaderGuid,
        string? sdiDeviceGuid,
        CancellationToken cancellationToken = default,
        string? firmwareBootGuid = null)
    {
        NativeUefiAndBcdRegistryEngine.RollbackRegistryAndUefiEntries(
            osLoaderGuid,
            sdiDeviceGuid,
            firmwareBootGuid,
            _logger);
        return Task.CompletedTask;
    }

    public Task<ActiveBcdState> InspectActiveBcdAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(NativeUefiAndBcdRegistryEngine.InspectSystemBcdFromRegistry());
    }

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
