using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Abstractions;

public sealed record CreatedInstallerPartitionInfo(
    int DiskNumber,
    int PartitionNumber,
    string PartitionGuid,
    string DriveLetter,
    long OffsetBytes,
    long SizeBytes);

public interface IPartitionService
{
    Task SuspendBitLockerForNextRebootAsync(
        string volumeLetter,
        CancellationToken cancellationToken = default);

    Task OptimizeOsVolumeBeforeShrinkAsync(
        string volumeLetter,
        StorageDriveKind driveKind,
        CancellationToken cancellationToken = default);

    Task<CreatedInstallerPartitionInfo> ShrinkOsAndCreateFat32InstallerPartitionAsync(
        int diskNumber,
        int osPartitionNumber,
        string expectedOsPartitionGuid,
        long newOsPartitionSizeBytes,
        long installerPartitionSizeBytes,
        string volumeLabel,
        CancellationToken cancellationToken = default);

    Task WriteInstallerManifestAsync(
        string installerDriveLetter,
        InstallerManifest manifest,
        CancellationToken cancellationToken = default);

    Task DeleteInstallerPartitionAsync(
        int diskNumber,
        int installerPartitionNumber,
        string expectedPartitionGuid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Grows the OS partition into the space left behind by a deleted installer partition.
    /// </summary>
    /// <param name="expectedOsPartitionOffsetBytes">
    /// Starting offset of the OS partition as recorded when the disk was inspected. When non-zero it is
    /// re-validated before any resize, so a stale partition number cannot grow a different partition.
    /// </param>
    Task ExtendOsPartitionIntoReclaimedSpaceAsync(
        int diskNumber,
        int osPartitionNumber,
        CancellationToken cancellationToken = default,
        long expectedOsPartitionOffsetBytes = 0);
}
