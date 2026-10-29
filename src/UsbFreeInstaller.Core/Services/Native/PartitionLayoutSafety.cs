namespace UsbFreeInstaller.Core.Services.Native;

// A formatter must never guess which of several volumes/partitions is the installer.
internal static class PartitionLayoutSafety
{
    internal static bool IsSamePartition(RawPartitionLayoutEntry expected, RawPartitionLayoutEntry current) =>
        current.PartitionStyle == expected.PartitionStyle && current.StartingOffsetBytes == expected.StartingOffsetBytes &&
        (current.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT
            ? expected.GptPartitionId != Guid.Empty && current.GptPartitionId == expected.GptPartitionId
            : current.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR &&
              current.MbrPartitionType == expected.MbrPartitionType &&
              current.MbrBootIndicator == expected.MbrBootIndicator &&
              current.MbrHiddenSectors == expected.MbrHiddenSectors &&
              (expected.MbrPartitionId == Guid.Empty || current.MbrPartitionId == expected.MbrPartitionId));

    internal static RawPartitionLayoutEntry VerifyBeforeFormatting(
        RawDiskLayoutSnapshot originalDisk, RawPartitionLayoutEntry expectedOs, RawDiskLayoutSnapshot current,
        int diskNumber, long installerStart, long installerLength, Guid installerGuid,
        int volumeDisk, long volumeStart, long volumeLength)
    {
        if (current.PartitionStyle != originalDisk.PartitionStyle ||
            (current.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT && current.GptDiskId != originalDisk.GptDiskId) ||
            (current.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR && current.MbrSignature != originalDisk.MbrSignature))
            throw new InvalidOperationException("Disk identity changed before formatting; an unformatted partition may remain.");
        if (installerStart < 0 || installerLength <= 0 || volumeDisk != diskNumber ||
            volumeStart != installerStart || volumeLength != installerLength)
            throw new InvalidOperationException("The installer volume's single extent does not match the new partition; it was not formatted.");

        RawPartitionLayoutEntry[] osMatches = current.Partitions.Where(p => IsSamePartition(expectedOs, p)).ToArray();
        RawPartitionLayoutEntry[] installerMatches = current.Partitions.Where(p => p.StartingOffsetBytes == installerStart && !p.IsMbrExtendedContainer).ToArray();
        if (osMatches.Length != 1 || installerMatches.Length != 1 ||
            osMatches[0].PartitionLengthBytes != expectedOs.PartitionLengthBytes ||
            installerMatches[0].PartitionLengthBytes != installerLength ||
            (current.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR && installerMatches[0].MbrPartitionType != 0x0C) ||
            (current.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT &&
             (installerGuid == Guid.Empty || installerMatches[0].GptPartitionId != installerGuid)))
            throw new InvalidOperationException("The OS or installer partition changed before formatting; it was not formatted.");

        long installerEnd = checked(installerStart + installerLength);
        long osEnd = checked(osMatches[0].StartingOffsetBytes + osMatches[0].PartitionLengthBytes);
        if (osEnd > installerStart || current.Partitions.Any(p => p != installerMatches[0] && p.PartitionLengthBytes > 0 &&
            p.StartingOffsetBytes < installerEnd && checked(p.StartingOffsetBytes + p.PartitionLengthBytes) > installerStart))
            throw new InvalidOperationException("The installer partition overlaps another partition; it was not formatted.");
        return osMatches[0];
    }
}
