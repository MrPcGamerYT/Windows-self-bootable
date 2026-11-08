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

    /// <summary>
    /// Resolve an installer by its persisted disk identity, never by a partition number alone.
    /// Windows Setup can renumber partitions after preparation; a stale number must not delete
    /// a newly created OS/recovery partition. MBR has no partition GUID, so SWBI uses its
    /// disk signature + starting offset token and requires an inactive primary FAT32 slot.
    /// </summary>
    internal static RawPartitionLayoutEntry ResolveInstallerForDeletion(
        RawDiskLayoutSnapshot layout, string expectedPartitionIdentity)
    {
        if (string.IsNullOrWhiteSpace(expectedPartitionIdentity))
            throw new InvalidOperationException("Installer partition identity is missing; refusing deletion by number alone.");

        RawPartitionLayoutEntry[] matches;
        if (layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT && layout.GptDiskId != Guid.Empty &&
            Guid.TryParse(expectedPartitionIdentity, out Guid id) && id != Guid.Empty)
        {
            matches = layout.Partitions.Where(p => p.GptPartitionId == id &&
                p.GptPartitionType == Win32StorageInterop.GptBasicDataTypeGuid).ToArray();
        }
        else if (layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR && layout.MbrSignature != 0)
        {
            matches = layout.Partitions.Where(p => p.RawSlotIndex is >= 0 and < 4 &&
                p.MbrPartitionType == 0x0C && !p.MbrBootIndicator &&
                string.Equals($"MBR:{layout.MbrSignature:X8}:{p.StartingOffsetBytes:X12}",
                    expectedPartitionIdentity, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        else
        {
            throw new InvalidOperationException("Installer identity is invalid for this disk's partition style.");
        }

        if (matches.Length != 1)
            throw new InvalidOperationException("Installer partition identity is missing, changed, or ambiguous; no partition was deleted.");
        return matches[0];
    }

    /// <summary>
    /// A successful IOCTL call alone does not show that the target is actually gone.
    /// Confirm the same physical disk, no partition at the deleted offset/GUID, and
    /// every other pre-existing partition unchanged (numbering may have changed).
    /// </summary>
    internal static bool WasInstallerDeleted(RawDiskLayoutSnapshot before, RawDiskLayoutSnapshot after,
        RawPartitionLayoutEntry target)
    {
        if (before.PartitionStyle != after.PartitionStyle ||
            (before.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT &&
             (before.GptDiskId == Guid.Empty || before.GptDiskId != after.GptDiskId)) ||
            (before.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR &&
             (before.MbrSignature == 0 || before.MbrSignature != after.MbrSignature)))
            return false;

        if (after.Partitions.Any(p => p.StartingOffsetBytes == target.StartingOffsetBytes ||
            (before.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT &&
             p.GptPartitionId == target.GptPartitionId)))
            return false;

        return before.Partitions.Where(p => p != target).All(p => after.Partitions.Any(other =>
            other.PartitionLengthBytes == p.PartitionLengthBytes &&
            (before.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT
                ? IsSamePartition(p, other) && other.GptPartitionType == p.GptPartitionType &&
                  other.GptAttributes == p.GptAttributes
                : other.StartingOffsetBytes == p.StartingOffsetBytes &&
                  other.MbrPartitionType == p.MbrPartitionType &&
                  other.MbrBootIndicator == p.MbrBootIndicator &&
                  other.MbrHiddenSectors == p.MbrHiddenSectors)));
    }

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
