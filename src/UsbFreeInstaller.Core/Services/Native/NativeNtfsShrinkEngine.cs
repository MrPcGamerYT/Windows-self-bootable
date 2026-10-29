using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UsbFreeInstaller.Core.Abstractions;

namespace UsbFreeInstaller.Core.Services.Native;

public readonly record struct NtfsVolumeGeometry(
    long TotalSectors,
    long TotalClusters,
    long FreeClusters,
    int BytesPerSector,
    int BytesPerCluster,
    long MftStartLcn,
    long Mft2StartLcn);

/// <summary>
/// Direct Win32 Kernel NTFS online shrink, extend, and tail-cluster evacuation engine.
/// Uses FSCTL_GET_NTFS_VOLUME_DATA, FSCTL_GET_VOLUME_BITMAP, FSCTL_GET_RETRIEVAL_POINTERS, FSCTL_MOVE_FILE,
/// FSCTL_SHRINK_VOLUME, IOCTL_DISK_GROW_PARTITION, and FSCTL_EXTEND_VOLUME directly against ntfs.sys and partmgr.sys —
/// zero dependence on powershell.exe, diskpart.exe, defrag.exe, or WMI.
/// </summary>
public static class NativeNtfsShrinkEngine
{
    private const uint ShrinkPrepare = 1;
    private const uint ShrinkCommit = 2;
    private const uint ShrinkAbort = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOVE_FILE_DATA
    {
        public IntPtr FileHandle;
        public long StartingVcn;
        public long StartingLcn;
        public uint ClusterCount;
    }

    public static bool TryQueryNtfsGeometry(SafeFileHandle volumeHandle, out NtfsVolumeGeometry geometry)
    {
        geometry = default;
        byte[] outBuf = new byte[128]; // NTFS_VOLUME_DATA_BUFFER is 96 bytes
        if (!Win32StorageInterop.DeviceIoControl(
                volumeHandle,
                Win32StorageInterop.FSCTL_GET_NTFS_VOLUME_DATA,
                null,
                0,
                outBuf,
                outBuf.Length,
                out int ret,
                IntPtr.Zero) || ret < 96)
        {
            return false;
        }

        long numSectors = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(8, 8));
        long totalClusters = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(16, 8));
        long freeClusters = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(24, 8));
        int bytesPerSec = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(40, 4));
        int bytesPerClus = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(44, 4));
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(64, 8));
        long mft2Lcn = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(72, 8));

        if (bytesPerSec <= 0 || bytesPerClus <= 0 || totalClusters <= 0)
        {
            return false;
        }

        geometry = new NtfsVolumeGeometry(
            numSectors,
            totalClusters,
            freeClusters,
            bytesPerSec,
            bytesPerClus,
            mftLcn,
            mft2Lcn);
        return true;
    }

    /// <summary>
    /// Computes the minimum supported OS partition size in <1 ms using direct NTFS kernel metadata
    /// and unmovable paging/hibernation file extent checks (no WMI, no defragsvc, no diskpart).
    /// </summary>
    public static long QueryMinimumShrinkSizeBytes(
        string driveLetter,
        long partitionSizeBytes,
        long volumeFreeBytes)
    {
        string clean = driveLetter.TrimEnd(':', '\\', '/').ToUpperInvariant();
        using SafeFileHandle volHandle = Win32StorageInterop.CreateFileW(
            $@"\\.\{clean}:",
            Win32StorageInterop.GENERIC_READ,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        long usedBytes = Math.Max(0L, partitionSizeBytes - volumeFreeBytes);
        long minFloorBytes = usedBytes + (2L * 1024 * 1024 * 1024); // Used + 2 GiB NTFS metadata buffer

        if (volHandle.IsInvalid || !TryQueryNtfsGeometry(volHandle, out NtfsVolumeGeometry geo))
        {
            return Math.Min(partitionSizeBytes, minFloorBytes);
        }

        long ntfsUsedBytes = (geo.TotalClusters - geo.FreeClusters) * geo.BytesPerCluster;
        long minBytes = Math.Max(minFloorBytes, ntfsUsedBytes + (2L * 1024 * 1024 * 1024));

        // Respect $MFT and $MFTMirr unmovable kernel metadata locations
        long mftEndBytes = (Math.Max(geo.MftStartLcn, geo.Mft2StartLcn) + 8192) * geo.BytesPerCluster;
        if (mftEndBytes > minBytes && mftEndBytes < partitionSizeBytes)
        {
            minBytes = mftEndBytes;
        }

        // Check active kernel paging/hibernation files (cannot be moved while active via FSCTL_MOVE_FILE)
        string[] pinnedSystemFiles =
        {
            $@"{clean}:\pagefile.sys",
            $@"{clean}:\hiberfil.sys",
            $@"{clean}:\swapfile.sys"
        };

        foreach (string sysFile in pinnedSystemFiles)
        {
            long highestLcn = TryGetHighestAllocatedClusterOfFile(sysFile);
            if (highestLcn > 0)
            {
                long pinnedEndBytes = (highestLcn + 4096) * geo.BytesPerCluster;
                if (pinnedEndBytes > minBytes && pinnedEndBytes <= partitionSizeBytes)
                {
                    minBytes = pinnedEndBytes;
                }
            }
        }

        return Math.Clamp(minBytes, 4L * 1024 * 1024 * 1024, partitionSizeBytes);
    }

    public static void EvacuateTailClustersIfNeeded(
        string driveLetter,
        long targetPartitionSizeBytes,
        ISafeLogger logger)
    {
        Win32StorageInterop.EnableProcessPrivilege("SeManageVolumePrivilege");
        Win32StorageInterop.EnableProcessPrivilege("SeBackupPrivilege");
        Win32StorageInterop.EnableProcessPrivilege("SeRestorePrivilege");

        string clean = driveLetter.TrimEnd(':', '\\', '/').ToUpperInvariant();
        using SafeFileHandle volHandle = Win32StorageInterop.CreateFileW(
            $@"\\.\{clean}:",
            Win32StorageInterop.GENERIC_READ | Win32StorageInterop.GENERIC_WRITE,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (volHandle.IsInvalid || !TryQueryNtfsGeometry(volHandle, out NtfsVolumeGeometry geo))
        {
            return;
        }

        long boundaryLcn = (targetPartitionSizeBytes / geo.BytesPerCluster) - 256;
        if (boundaryLcn <= 0 || boundaryLcn >= geo.TotalClusters)
        {
            return;
        }

        if (!HasAllocatedClustersAbove(volHandle, boundaryLcn))
        {
            logger.Info("NativeShrink", $"Tail zone above LCN {boundaryLcn:N0} on {clean}: is already clear (0 clusters to evacuate).");
            return;
        }

        logger.Info("NativeShrink", $"Evacuating movable NTFS clusters above LCN {boundaryLcn:N0} on {clean}: via kernel FSCTL_MOVE_FILE...");
        long freeSearchCursorLcn = Math.Max(4096L, geo.MftStartLcn + 32768L);
        int movedFiles = 0;

        // Relocate NTFS Change Journal stream if it allocated sparse runs in the tail zone
        if (TryRelocateItemAboveBoundary(volHandle, $@"{clean}:\$Extend\$UsnJrnl:$J", isDirectory: false, boundaryLcn, ref freeSearchCursorLcn))
        {
            movedFiles++;
        }

        string rootPath = $@"{clean}:\";
        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            string currentDir = stack.Pop();

            // Also relocate directory index allocation streams if any sit in the tail zone
            if (!string.Equals(currentDir, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                if (TryRelocateItemAboveBoundary(volHandle, currentDir, isDirectory: true, boundaryLcn, ref freeSearchCursorLcn))
                {
                    movedFiles++;
                }
            }

            try
            {
                foreach (string subDir in Directory.EnumerateDirectories(currentDir))
                {
                    FileAttributes attr = File.GetAttributes(subDir);
                    if ((attr & FileAttributes.ReparsePoint) != 0)
                    {
                        continue; // Never follow symlinks or junctions
                    }
                    stack.Push(subDir);
                }
            }
            catch
            {
                // Skip protected/inaccessible kernel directories
            }

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(currentDir))
                {
                    string name = Path.GetFileName(filePath);
                    if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryRelocateItemAboveBoundary(volHandle, filePath, isDirectory: false, boundaryLcn, ref freeSearchCursorLcn))
                    {
                        movedFiles++;
                    }
                }
            }
            catch
            {
                // Skip locked files
            }
        }

        logger.Info("NativeShrink", $"Relocated {movedFiles} tail file/directory stream(s) below shrink boundary on {clean}:.");
    }

    public static void ShrinkOnlinePartition(
        int diskNumber,
        int osPartitionNumber,
        string driveLetter,
        long newOsPartitionSizeBytes,
        ISafeLogger logger)
    {
        string clean = driveLetter.TrimEnd(':', '\\', '/').ToUpperInvariant();
        const long oneMb = 1024L * 1024L;
        long alignedNewSize = (newOsPartitionSizeBytes / oneMb) * oneMb;
        if (alignedNewSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newOsPartitionSizeBytes));
        }

        using SafeFileHandle volHandle = Win32StorageInterop.CreateFileW(
            $@"\\.\{clean}:",
            Win32StorageInterop.GENERIC_READ | Win32StorageInterop.GENERIC_WRITE,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (volHandle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"Failed to open volume {clean}: for shrinking (Win32 error {err}).");
        }

        using SafeFileHandle diskHandle = Win32StorageInterop.CreateFileW(
            $@"\\.\PhysicalDrive{diskNumber}",
            Win32StorageInterop.GENERIC_READ | Win32StorageInterop.GENERIC_WRITE,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (diskHandle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"Failed to open PhysicalDrive{diskNumber} (Win32 error {err}).");
        }

        RawDiskLayoutSnapshot layout = Win32StorageInterop.TryReadDiskLayout(diskHandle)
            ?? throw new InvalidOperationException($"Unable to read partition table on PhysicalDrive{diskNumber}.");
        RawPartitionLayoutEntry osPart = layout.Partitions.FirstOrDefault(p => p.PartitionNumber == osPartitionNumber)
            ?? throw new InvalidOperationException($"Partition {osPartitionNumber} was not found on PhysicalDrive{diskNumber}.");

        VerifyVolumeIdentity(volHandle, diskNumber, osPart);
        VerifyNtfsFits(volHandle, osPart.PartitionLengthBytes);
        if (osPart.PartitionLengthBytes <= alignedNewSize)
        {
            logger.Info("NativeShrink", $"Partition {osPartitionNumber} is already no larger than the requested size.");
            return;
        }

        if (!TryQueryNtfsGeometry(volHandle, out NtfsVolumeGeometry geometry) ||
            geometry.BytesPerCluster <= 0 || geometry.BytesPerCluster % geometry.BytesPerSector != 0 ||
            alignedNewSize <= geometry.BytesPerCluster || alignedNewSize % geometry.BytesPerSector != 0)
        {
            throw new InvalidOperationException("Cannot determine valid NTFS geometry for the requested shrink.");
        }

        Win32StorageInterop.EnableProcessPrivilege("SeManageVolumePrivilege");
        Win32StorageInterop.EnableProcessPrivilege("SeBackupPrivilege");
        Win32StorageInterop.EnableProcessPrivilege("SeRestorePrivilege");

        // Reserve a cluster for the backup boot sector. Prepare blocks allocations beyond this boundary.
        long newNtfsSectors = (alignedNewSize - geometry.BytesPerCluster) / geometry.BytesPerSector;
        var operations = new NativeOnlineShrinkOperations(
            volHandle, diskHandle, layout, osPart, diskNumber, clean, alignedNewSize, newNtfsSectors, logger);
        OnlineShrinkTransaction.Execute(operations);
        logger.Info("NativeShrink", $"Verified Disk {diskNumber} Partition {osPartitionNumber} ({clean}:) after online shrink to {alignedNewSize / oneMb:N0} MB.");
    }

    // Called again immediately before writing a new partition entry, including when no shrink was needed.
    public static void VerifyNtfsFitsPartition(string driveLetter, int diskNumber, RawPartitionLayoutEntry partition)
    {
        string clean = driveLetter.TrimEnd(':', '\\', '/').ToUpperInvariant();
        using SafeFileHandle volume = Win32StorageInterop.CreateFileW(
            $@"\\.\{clean}:", Win32StorageInterop.GENERIC_READ,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
            IntPtr.Zero, Win32StorageInterop.OPEN_EXISTING, 0, IntPtr.Zero);
        if (volume.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot verify NTFS volume {clean}: before creating a partition.");
        }

        VerifyVolumeIdentity(volume, diskNumber, partition);
        VerifyNtfsFits(volume, partition.PartitionLengthBytes);
    }

    private static void VerifyVolumeIdentity(SafeFileHandle volume, int diskNumber, RawPartitionLayoutEntry partition)
    {
        if (!Win32StorageInterop.TryGetSingleVolumeDiskExtent(volume, out int volumeDisk, out long volumeStart) ||
            volumeDisk != diskNumber || volumeStart != partition.StartingOffsetBytes)
        {
            throw new InvalidOperationException("The NTFS volume no longer matches the selected single-extent disk partition.");
        }
    }

    private static void VerifyNtfsFits(SafeFileHandle volume, long partitionBytes)
    {
        if (!TryQueryNtfsGeometry(volume, out NtfsVolumeGeometry geometry) ||
            geometry.BytesPerSector is not (512 or 1024 or 2048 or 4096))
        {
            throw new InvalidOperationException("Could not query valid NTFS volume geometry; refusing to create a partition.");
        }

        byte[] bootSector = new byte[geometry.BytesPerSector];
        if (!Win32StorageInterop.SetFilePointerEx(volume, 0, out _, 0) ||
            !Win32StorageInterop.ReadFile(volume, bootSector, (uint)bootSector.Length, out uint bytesRead, IntPtr.Zero) ||
            bytesRead != bootSector.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the NTFS $Boot sector for verification.");
        }

        NtfsBootSector.VerifyFits(NtfsBootSector.Parse(bootSector), geometry, partitionBytes);
    }

    private sealed class NativeOnlineShrinkOperations : IOnlineShrinkOperations
    {
        private readonly SafeFileHandle _volume;
        private readonly SafeFileHandle _disk;
        private readonly RawDiskLayoutSnapshot _originalLayout;
        private readonly RawPartitionLayoutEntry _originalPartition;
        private readonly int _diskNumber;
        private readonly string _driveLetter;
        private readonly long _targetBytes;
        private readonly long _targetSectors;
        private readonly ISafeLogger _logger;

        public NativeOnlineShrinkOperations(SafeFileHandle volume, SafeFileHandle disk,
            RawDiskLayoutSnapshot layout, RawPartitionLayoutEntry partition, int diskNumber,
            string driveLetter, long targetBytes, long targetSectors, ISafeLogger logger)
        {
            _volume = volume;
            _disk = disk;
            _originalLayout = layout;
            _originalPartition = partition;
            _diskNumber = diskNumber;
            _driveLetter = driveLetter;
            _targetBytes = targetBytes;
            _targetSectors = targetSectors;
            _logger = logger;
        }

        public void Prepare() => ShrinkControl(ShrinkPrepare, _targetSectors, "ShrinkPrepare");
        public void Evacuate() => EvacuateTailClustersIfNeeded(_driveLetter, _targetBytes, _logger);
        public void Commit() => ShrinkControl(ShrinkCommit, 0, "ShrinkCommit");
        public void Abort() => ShrinkControl(ShrinkAbort, 0, "ShrinkAbort");

        public void VerifyFileSystemFitsTarget()
        {
            if (!Win32StorageInterop.FlushFileBuffers(_volume))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not flush the committed NTFS volume.");
            }

            VerifyVolumeIdentity(_volume, _diskNumber, _originalPartition);
            VerifyNtfsFits(_volume, _targetBytes);
            RawPartitionLayoutEntry current = ReadOriginalPartition();
            if (current.PartitionNumber != _originalPartition.PartitionNumber ||
                current.PartitionLengthBytes != _originalPartition.PartitionLengthBytes)
            {
                throw new InvalidOperationException("The partition changed during the NTFS shrink; refusing to resize it.");
            }
        }

        public void ResizePartition() => ChangePartitionLength(
            _originalPartition.PartitionNumber, _targetBytes - _originalPartition.PartitionLengthBytes);

        public void VerifyResult()
        {
            Win32StorageInterop.TryControlCode(_disk, Win32StorageInterop.IOCTL_DISK_UPDATE_PROPERTIES);
            RawPartitionLayoutEntry current = ReadOriginalPartition();
            if (current.PartitionLengthBytes != _targetBytes)
            {
                throw new InvalidOperationException("The disk did not report the requested partition size after shrink.");
            }

            VerifyVolumeIdentity(_volume, _diskNumber, current);
            VerifyNtfsFits(_volume, current.PartitionLengthBytes);
        }

        public bool TryRestorePartitionLength()
        {
            RawDiskLayoutSnapshot currentLayout = ReadMatchingLayout();
            RawPartitionLayoutEntry? current = FindMatchingPartition(currentLayout);
            if (current is null || current.PartitionLengthBytes > _originalPartition.PartitionLengthBytes)
            {
                return false;
            }

            if (current.PartitionLengthBytes == _originalPartition.PartitionLengthBytes)
            {
                return true;
            }

            long currentEnd = checked(current.StartingOffsetBytes + current.PartitionLengthBytes);
            long originalEnd = checked(_originalPartition.StartingOffsetBytes + _originalPartition.PartitionLengthBytes);
            if (currentLayout.Partitions.Any(p => p != current && p.PartitionLengthBytes > 0 &&
                p.StartingOffsetBytes < originalEnd && checked(p.StartingOffsetBytes + p.PartitionLengthBytes) > currentEnd))
            {
                return false;
            }

            ChangePartitionLength(current.PartitionNumber, _originalPartition.PartitionLengthBytes - current.PartitionLengthBytes);
            Win32StorageInterop.TryControlCode(_disk, Win32StorageInterop.IOCTL_DISK_UPDATE_PROPERTIES);
            return ReadOriginalPartition().PartitionLengthBytes == _originalPartition.PartitionLengthBytes;
        }

        private void ShrinkControl(uint request, long sectors, string operation)
        {
            if (!CallFsctlShrinkVolume(_volume, request, sectors, out int error))
            {
                throw new Win32Exception(error, $"{operation} failed on {_driveLetter}: (Win32 error {error}).");
            }
        }

        private void ChangePartitionLength(int partitionNumber, long deltaBytes)
        {
            byte[] input = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(0, 4), partitionNumber);
            BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(8, 8), deltaBytes);
            if (!Win32StorageInterop.DeviceIoControl(_disk, Win32StorageInterop.IOCTL_DISK_GROW_PARTITION,
                    input, input.Length, null, 0, out _, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"Partition resize failed (Win32 error {error}).");
            }
        }

        private RawDiskLayoutSnapshot ReadMatchingLayout()
        {
            RawDiskLayoutSnapshot current = Win32StorageInterop.TryReadDiskLayout(_disk)
                ?? throw new InvalidOperationException("Unable to re-read the disk layout.");
            if (current.PartitionStyle != _originalLayout.PartitionStyle ||
                (current.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT && current.GptDiskId != _originalLayout.GptDiskId) ||
                (current.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR && current.MbrSignature != _originalLayout.MbrSignature))
            {
                throw new InvalidOperationException("Disk identity changed while shrinking.");
            }
            return current;
        }

        private RawPartitionLayoutEntry? FindMatchingPartition(RawDiskLayoutSnapshot layout) =>
            layout.Partitions.FirstOrDefault(p => PartitionLayoutSafety.IsSamePartition(_originalPartition, p));

        private RawPartitionLayoutEntry ReadOriginalPartition() => FindMatchingPartition(ReadMatchingLayout())
            ?? throw new InvalidOperationException("The original NTFS partition could not be re-identified.");
    }

    public static void ExtendOnlinePartitionToMaxAdjacent(
        int diskNumber,
        int osPartitionNumber,
        string driveLetter,
        ISafeLogger logger,
        long expectedOsStartOffsetBytes = 0,
        Guid expectedOsPartitionGuid = default)
    {
        string clean = driveLetter.TrimEnd(':', '\\', '/').ToUpperInvariant();
        const long oneMb = 1024L * 1024L;

        using SafeFileHandle diskHandle = Win32StorageInterop.CreateFileW(
            $@"\\.\PhysicalDrive{diskNumber}",
            Win32StorageInterop.GENERIC_READ | Win32StorageInterop.GENERIC_WRITE,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (diskHandle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"Unable to open PhysicalDrive{diskNumber} for extend (Win32 error {err}).");
        }

        long diskLength = Win32StorageInterop.QueryDeviceLengthBytes(diskHandle);
        RawDiskLayoutSnapshot layout = Win32StorageInterop.TryReadDiskLayout(diskHandle)
            ?? throw new InvalidOperationException($"Unable to read partition table on PhysicalDrive{diskNumber}.");

        RawPartitionLayoutEntry osPart = layout.Partitions.FirstOrDefault(p => p.PartitionNumber == osPartitionNumber)
            ?? throw new InvalidOperationException($"Partition {osPartitionNumber} not found on Disk {diskNumber}.");

        // The partition number is captured during inspection and the installer partition is deleted in
        // between, so re-numbering is possible. Require the caller's identity evidence before resizing.
        if (expectedOsStartOffsetBytes > 0 && Math.Abs(osPart.StartingOffsetBytes - expectedOsStartOffsetBytes) > 65536)
        {
            throw new InvalidOperationException(
                $"Partition {osPartitionNumber} on Disk {diskNumber} now starts at offset {osPart.StartingOffsetBytes}, " +
                $"not at the expected {expectedOsStartOffsetBytes}. Refusing to extend a different partition.");
        }

        if (expectedOsPartitionGuid != Guid.Empty && osPart.GptPartitionId != expectedOsPartitionGuid)
        {
            throw new InvalidOperationException(
                $"Partition {osPartitionNumber} on Disk {diskNumber} has GPT GUID {{{osPart.GptPartitionId:D}}}, " +
                $"not the expected {{{expectedOsPartitionGuid:D}}}. Refusing to extend a different partition.");
        }

        int diskSectorBytes = Win32StorageInterop.QueryLogicalSectorSize(diskHandle);
        long mbrCeilingBytes = 0xFFFF_FFFFL * Math.Max(512, diskSectorBytes);

        long osEnd = osPart.StartingOffsetBytes + osPart.PartitionLengthBytes;
        RawPartitionLayoutEntry? nextPart = layout.Partitions
            .Where(p => p.StartingOffsetBytes >= osEnd &&
                        (layout.PartitionStyle != Win32StorageInterop.PARTITION_STYLE_MBR || p.RawSlotIndex < 4))
            .OrderBy(p => p.StartingOffsetBytes)
            .FirstOrDefault();

        long maxEnd = nextPart?.StartingOffsetBytes
            ?? (layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT && layout.GptUsableLength > 0
                ? layout.GptStartingUsableOffset + layout.GptUsableLength
                : layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR
                    ? Math.Min(diskLength - oneMb, mbrCeilingBytes - oneMb)
                    : diskLength - oneMb);

        long alignedMaxEnd = (maxEnd / oneMb) * oneMb;
        long bytesToGrow = alignedMaxEnd - osEnd;
        if (bytesToGrow < oneMb)
        {
            logger.Info("NativeExtend", $"No adjacent unallocated space (>1 MB) after Partition {osPartitionNumber}.");
            return;
        }

        using SafeFileHandle volHandle = Win32StorageInterop.CreateFileW(
            $@"\\.\{clean}:",
            Win32StorageInterop.GENERIC_READ | Win32StorageInterop.GENERIC_WRITE,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (volHandle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err,
                $"Unable to open volume {clean}: to extend the filesystem (Win32 error {err}). " +
                "The partition has not been resized.");
        }

        // The drive letter must still describe exactly the partition we are about to grow.
        if (!Win32StorageInterop.TryGetSingleVolumeDiskExtent(volHandle, out int volumeDisk, out long volumeStart, out long volumeLength) ||
            volumeDisk != diskNumber ||
            volumeStart != osPart.StartingOffsetBytes ||
            volumeLength != osPart.PartitionLengthBytes)
        {
            throw new InvalidOperationException(
                $"{clean}: does not map to Disk {diskNumber} Partition {osPartitionNumber} " +
                $"(offset {osPart.StartingOffsetBytes}, length {osPart.PartitionLengthBytes}). " +
                "The partition table and volume mount points disagree; nothing was resized.");
        }

        byte[] growIn = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(growIn.AsSpan(0, 4), osPartitionNumber);
        BinaryPrimitives.WriteInt64LittleEndian(growIn.AsSpan(8, 8), bytesToGrow);

        if (!Win32StorageInterop.DeviceIoControl(
                diskHandle,
                Win32StorageInterop.IOCTL_DISK_GROW_PARTITION,
                growIn,
                growIn.Length,
                null,
                0,
                out _,
                IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"IOCTL_DISK_GROW_PARTITION (+{bytesToGrow}) failed (Win32 error {err}).");
        }

        Win32StorageInterop.TryControlCode(diskHandle, Win32StorageInterop.IOCTL_DISK_UPDATE_PROPERTIES);

        int bytesPerSector = Win32StorageInterop.QueryLogicalSectorSize(volHandle);
        long targetPartitionLength = osPart.PartitionLengthBytes + bytesToGrow;
        int clusterBytes = TryQueryNtfsGeometry(volHandle, out NtfsVolumeGeometry geometry) && geometry.BytesPerCluster > 0
            ? geometry.BytesPerCluster
            : 4096;

        long targetTotalSectors = targetPartitionLength / bytesPerSector;
        bool filesystemExtended = TryExtendFileSystem(volHandle, targetTotalSectors, out int extendError);

        if (!filesystemExtended)
        {
            // NTFS reserves its backup boot sector at the end of the volume; retry leaving one cluster free.
            long reducedSectors = Math.Max(0L, (targetPartitionLength - clusterBytes) / bytesPerSector);
            filesystemExtended = TryExtendFileSystem(volHandle, reducedSectors, out int retryError);
            if (!filesystemExtended)
            {
                int reported = retryError != 0 ? retryError : extendError;
                throw new Win32Exception(reported,
                    $"The partition was enlarged by +{bytesToGrow / oneMb:N0} MB but the filesystem on {clean}: could not be extended " +
                    $"(FSCTL_EXTEND_VOLUME Win32 error {reported}). The volume is now smaller than its partition, so the reclaimed " +
                    "space is not usable. Run 'chkdsk /f' on the volume, then re-run Post-Install Cleanup.");
            }
        }

        Win32StorageInterop.TryControlCode(volHandle, Win32StorageInterop.IOCTL_DISK_UPDATE_PROPERTIES);
        long verifiedVolumeLength = Win32StorageInterop.QueryDeviceLengthBytes(volHandle);
        if (verifiedVolumeLength < targetPartitionLength - clusterBytes)
        {
            throw new InvalidOperationException(
                $"Filesystem extension on {clean}: could not be verified. The volume reports " +
                $"{verifiedVolumeLength / oneMb:N0} MB inside a {targetPartitionLength / oneMb:N0} MB partition. " +
                "The reclaimed space is not usable; run 'chkdsk /f' on the volume and re-run Post-Install Cleanup.");
        }

        logger.Info(
            "NativeExtend",
            $"Verified extend on Disk {diskNumber} Partition {osPartitionNumber} ({clean}:): " +
            $"volume {osPart.PartitionLengthBytes / oneMb:N0} MB -> {verifiedVolumeLength / oneMb:N0} MB " +
            $"(+{bytesToGrow / oneMb:N0} MB of adjacent unallocated space reclaimed via native kernel IOCTLs).");
    }

    private static bool TryExtendFileSystem(SafeFileHandle volumeHandle, long newTotalSectors, out int win32Error)
    {
        byte[] extendIn = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(extendIn, newTotalSectors);

        if (Win32StorageInterop.DeviceIoControl(
                volumeHandle,
                Win32StorageInterop.FSCTL_EXTEND_VOLUME,
                extendIn,
                extendIn.Length,
                null,
                0,
                out _,
                IntPtr.Zero))
        {
            win32Error = 0;
            return true;
        }

        win32Error = Marshal.GetLastWin32Error();
        return false;
    }

    /// <summary>
    /// Invokes FSCTL_SHRINK_VOLUME using the exact 24-byte Win32 SHRINK_VOLUME_INFORMATION layout:
    ///   [0..3]   uint  ShrinkRequestType  (1 = ShrinkPrepare, 2 = ShrinkCommit, 3 = ShrinkAbort)
    ///   [4..7]   uint  Padding            (0)
    ///   [8..15]  ulong Flags              (0 — reserved, MUST be 0 or ntfs.sys returns ERROR_INVALID_PARAMETER = 87)
    ///   [16..23] long  NewNumberOfSectors
    /// </summary>
    private static bool CallFsctlShrinkVolume(SafeFileHandle volumeHandle, uint requestType, long newSectors, out int win32Error)
    {
        byte[] inBuf = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(inBuf.AsSpan(0, 4), requestType);
        BinaryPrimitives.WriteUInt64LittleEndian(inBuf.AsSpan(8, 8), 0UL);
        BinaryPrimitives.WriteInt64LittleEndian(inBuf.AsSpan(16, 8), newSectors);

        if (Win32StorageInterop.DeviceIoControl(
                volumeHandle,
                Win32StorageInterop.FSCTL_SHRINK_VOLUME,
                inBuf,
                inBuf.Length,
                null,
                0,
                out _,
                IntPtr.Zero))
        {
            win32Error = 0;
            return true;
        }

        win32Error = Marshal.GetLastWin32Error();
        return false;
    }

    private static bool HasAllocatedClustersAbove(SafeFileHandle volHandle, long startLcn)
    {
        byte[] inBuf = new byte[8];
        byte[] outBuf = new byte[64 * 1024];
        long cursor = startLcn & ~7L;

        while (true)
        {
            BinaryPrimitives.WriteInt64LittleEndian(inBuf, cursor);
            bool ok = Win32StorageInterop.DeviceIoControl(
                volHandle,
                Win32StorageInterop.FSCTL_GET_VOLUME_BITMAP,
                inBuf,
                inBuf.Length,
                outBuf,
                outBuf.Length,
                out int ret,
                IntPtr.Zero);

            int err = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && err != 234 /* ERROR_MORE_DATA */)
            {
                return false;
            }

            if (ret <= 16)
            {
                return false;
            }

            long actualStart = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(0, 8));
            long bitmapBits = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(8, 8));
            int availBytes = Math.Min(ret - 16, (int)((bitmapBits + 7) / 8));

            for (int i = 0; i < availBytes; i++)
            {
                byte b = outBuf[16 + i];
                if (b == 0) continue;

                for (int bit = 0; bit < 8; bit++)
                {
                    if ((b & (1 << bit)) != 0)
                    {
                        long lcn = actualStart + (i * 8L) + bit;
                        if (lcn >= startLcn && lcn < actualStart + bitmapBits)
                        {
                            return true;
                        }
                    }
                }
            }

            if (ok)
            {
                return false;
            }

            cursor = actualStart + (availBytes * 8L);
        }
    }

    private static bool TryRelocateItemAboveBoundary(
        SafeFileHandle volHandle,
        string itemPath,
        bool isDirectory,
        long boundaryLcn,
        ref long freeSearchCursorLcn)
    {
        uint flags = isDirectory ? Win32StorageInterop.FILE_FLAG_BACKUP_SEMANTICS : 0;
        using SafeFileHandle fh = Win32StorageInterop.CreateFileW(
            itemPath,
            Win32StorageInterop.FILE_READ_ATTRIBUTES | Win32StorageInterop.SYNCHRONIZE,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            flags,
            IntPtr.Zero);

        if (fh.IsInvalid)
        {
            return false;
        }

        List<(long Vcn, long Lcn, long Count)> extents = GetFileAllocatedExtents(fh);
        bool movedAny = false;

        foreach ((long vcn, long lcn, long count) in extents)
        {
            if (lcn + count <= boundaryLcn)
            {
                continue;
            }

            // Move in chunks of up to 4096 clusters (16 MB) so even fragmented free space easily accommodates large files
            long offsetInExtent = 0;
            while (offsetInExtent < count)
            {
                uint chunk = (uint)Math.Min(4096L, count - offsetInExtent);
                if (!TryFindFreeClusterRunBelow(volHandle, boundaryLcn, chunk, ref freeSearchCursorLcn, out long targetFreeLcn))
                {
                    break;
                }

                var moveData = new MOVE_FILE_DATA
                {
                    FileHandle = fh.DangerousGetHandle(),
                    StartingVcn = vcn + offsetInExtent,
                    StartingLcn = targetFreeLcn,
                    ClusterCount = chunk
                };

                if (!CallMoveFileIoctl(volHandle, ref moveData))
                {
                    break;
                }

                freeSearchCursorLcn = targetFreeLcn + chunk;
                offsetInExtent += chunk;
                movedAny = true;
            }
        }

        return movedAny;
    }

    private static bool CallMoveFileIoctl(SafeFileHandle volHandle, ref MOVE_FILE_DATA moveData)
    {
        int size = Marshal.SizeOf<MOVE_FILE_DATA>();
        byte[] buf = new byte[size];
        MemoryMarshal.Write(buf.AsSpan(), in moveData);
        return Win32StorageInterop.DeviceIoControl(
            volHandle,
            Win32StorageInterop.FSCTL_MOVE_FILE,
            buf,
            buf.Length,
            null,
            0,
            out _,
            IntPtr.Zero);
    }

    private static bool TryFindFreeClusterRunBelow(
        SafeFileHandle volHandle,
        long maxBoundaryLcn,
        uint neededClusters,
        ref long searchStartLcn,
        out long foundLcn)
    {
        foundLcn = -1;
        byte[] inBuf = new byte[8];
        byte[] outBuf = new byte[64 * 1024];

        for (int pass = 0; pass < 2; pass++)
        {
            long cursor = (pass == 0 ? searchStartLcn : 2048L) & ~7L;
            long limit = pass == 0 ? maxBoundaryLcn : Math.Min(maxBoundaryLcn, searchStartLcn + neededClusters);

            while (cursor < limit)
            {
                BinaryPrimitives.WriteInt64LittleEndian(inBuf, cursor);
                bool ok = Win32StorageInterop.DeviceIoControl(
                    volHandle,
                    Win32StorageInterop.FSCTL_GET_VOLUME_BITMAP,
                    inBuf,
                    inBuf.Length,
                    outBuf,
                    outBuf.Length,
                    out int ret,
                    IntPtr.Zero);

                int err = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && err != 234) break;
                if (ret <= 16) break;

                long actualStart = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(0, 8));
                long bitmapBits = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(8, 8));
                int availBytes = Math.Min(ret - 16, (int)((bitmapBits + 7) / 8));

                long runStart = -1;
                long runLen = 0;

                for (int i = 0; i < availBytes; i++)
                {
                    byte b = outBuf[16 + i];
                    if (b == 0xFF)
                    {
                        runStart = -1;
                        runLen = 0;
                        continue;
                    }

                    if (b == 0x00)
                    {
                        long baseLcn = actualStart + (i * 8L);
                        if (baseLcn + 8 > maxBoundaryLcn) break;
                        if (runStart < 0) runStart = baseLcn;
                        runLen += 8;
                        if (runLen >= neededClusters)
                        {
                            foundLcn = runStart;
                            return true;
                        }
                        continue;
                    }

                    for (int bit = 0; bit < 8; bit++)
                    {
                        long lcn = actualStart + (i * 8L) + bit;
                        if (lcn >= maxBoundaryLcn) break;
                        if ((b & (1 << bit)) == 0)
                        {
                            if (runStart < 0) runStart = lcn;
                            runLen++;
                            if (runLen >= neededClusters)
                            {
                                foundLcn = runStart;
                                return true;
                            }
                        }
                        else
                        {
                            runStart = -1;
                            runLen = 0;
                        }
                    }
                }

                if (ok) break;
                cursor = actualStart + (availBytes * 8L);
            }
        }

        return false;
    }

    private static long TryGetHighestAllocatedClusterOfFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return -1;
        }

        using SafeFileHandle fh = Win32StorageInterop.CreateFileW(
            filePath,
            Win32StorageInterop.FILE_READ_ATTRIBUTES | Win32StorageInterop.SYNCHRONIZE,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (fh.IsInvalid)
        {
            return -1;
        }

        long highest = -1;
        foreach ((_, long lcn, long count) in GetFileAllocatedExtents(fh))
        {
            if (lcn + count > highest)
            {
                highest = lcn + count;
            }
        }

        return highest;
    }

    private static List<(long Vcn, long Lcn, long Count)> GetFileAllocatedExtents(SafeFileHandle fileHandle)
    {
        var list = new List<(long, long, long)>();
        byte[] inBuf = new byte[8];
        byte[] outBuf = new byte[16 * 1024];
        long startVcn = 0;

        while (true)
        {
            BinaryPrimitives.WriteInt64LittleEndian(inBuf, startVcn);
            bool ok = Win32StorageInterop.DeviceIoControl(
                fileHandle,
                Win32StorageInterop.FSCTL_GET_RETRIEVAL_POINTERS,
                inBuf,
                inBuf.Length,
                outBuf,
                outBuf.Length,
                out int ret,
                IntPtr.Zero);

            int err = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && err != 234 /* ERROR_MORE_DATA */)
            {
                break;
            }

            if (ret < 32)
            {
                break;
            }

            int extentCount = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(0, 4));
            long curVcn = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(8, 8));
            int maxExtents = (ret - 16) / 16;
            int safeExtents = Math.Min(extentCount, maxExtents);

            for (int i = 0; i < safeExtents; i++)
            {
                int off = 16 + (i * 16);
                long nextVcn = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(off, 8));
                long lcn = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(off + 8, 8));
                long count = nextVcn - curVcn;
                if (lcn >= 0 && count > 0)
                {
                    list.Add((curVcn, lcn, count));
                }
                curVcn = nextVcn;
            }

            if (ok)
            {
                break;
            }

            startVcn = curVcn;
        }

        return list;
    }
}
