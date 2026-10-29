using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Abstractions;

public sealed record DeploymentProgress(
    string Phase,
    int Percent,
    string CurrentItem);

public interface IIsoDeploymentService
{
    Task DeployIsoToInstallerPartitionAsync(
        IsoInspectionResult iso,
        string targetDriveLetter,
        int copyBufferSizeBytes = 1024 * 1024,
        bool writeEditionUnlockEiCfg = true,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool bypassWin11HardwareChecks = false);

    Task VerifyDeployedBootAndInstallFilesAsync(
        IsoInspectionResult iso,
        string targetDriveLetter,
        CancellationToken cancellationToken = default);
}

public sealed record DriverHarvestSummary(
    int StorageDriverInfCount,
    int NetworkDriverInfCount,
    long TotalExportedBytes,
    bool InjectedIntoBootWim);

public interface IDriverHarvesterService
{
    Task<DriverHarvestSummary> ExportAndInjectDriversAsync(
        string installerDriveLetter,
        bool includeNetworkDrivers,
        bool injectStorageIntoBootWim,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task LaunchInPlaceUpgradeSetupAsync(
        string installerDriveLetter,
        CancellationToken cancellationToken = default);
}

public sealed record BcdProvisionResult(
    string? BcdBackupFilePath,
    string? SdiDeviceGuid,
    string? OsLoaderGuid,
    string? FirmwareBootGuid = null);

public sealed record ActiveBcdState(
    bool CurrentBootIsRamdisk,
    string CurrentDeviceRaw,
    string CurrentOsDeviceRaw,
    string? ExistingUsbFreeLoaderGuid,
    string? ExistingUsbFreeSdiGuid,
    string? ExistingFirmwareBootGuid = null);

public interface IBcdBootService
{
    Task<BcdProvisionResult> ConfigureOneTimeWinPeBootAsync(
        string installerDriveLetter,
        string backupDirectory,
        FirmwareMode firmwareMode = FirmwareMode.Uefi,
        CancellationToken cancellationToken = default);

    Task RollbackBcdEntriesAsync(
        string? osLoaderGuid,
        string? sdiDeviceGuid,
        CancellationToken cancellationToken = default,
        string? firmwareBootGuid = null);

    Task<ActiveBcdState> InspectActiveBcdAsync(
        CancellationToken cancellationToken = default);

    Task RebootSystemAsync(
        int delaySeconds = 5,
        CancellationToken cancellationToken = default);
}
