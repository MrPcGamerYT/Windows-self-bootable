namespace UsbFreeInstaller.Core.Services.Native;

/// <summary>
/// NTFS's TotalSectors is filesystem geometry. A volume device's IOCTL_DISK_GET_LENGTH_INFO
/// reports the enclosing partition instead and cannot prove FSCTL_EXTEND_VOLUME succeeded.
/// </summary>
internal static class NtfsExtendSafety
{
    internal static bool IsFileSystemExtended(long partitionBytes, int clusterBytes, long ntfsBytes) =>
        partitionBytes > 0 && clusterBytes > 0 && ntfsBytes > 0 &&
        ntfsBytes <= partitionBytes && partitionBytes - ntfsBytes <= clusterBytes;
}
