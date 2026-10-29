using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UsbFreeInstaller.Core.Services.Native;

/// <summary>
/// Pure C# Microsoft-EFI-spec-compliant FAT32 formatter.
/// Formats any partition (up to 32 GiB+) directly via raw Win32 volume handle (\\.\W: or \\?\Volume{guid})
/// without invoking format.com, Format-Volume, diskpart.exe, or powershell.exe.
/// </summary>
public static class NativeFat32Formatter
{
    public const int MinFat32ClusterCount = 65_525;

    public static void FormatVolumeByDriveLetter(
        string driveLetter,
        string volumeLabel,
        long partitionOffsetBytes = 0)
    {
        string clean = driveLetter.TrimEnd(':', '\\', '/').ToUpperInvariant();
        string devicePath = $@"\\.\{clean}:";
        FormatVolumeDevicePath(devicePath, volumeLabel, partitionOffsetBytes);
    }

    public static void FormatVolumeDevicePath(
        string volumeDevicePath,
        string volumeLabel,
        long partitionOffsetBytes = 0,
        int? expectedDiskNumber = null,
        long? expectedVolumeLengthBytes = null,
        int? expectedSectorBytes = null)
    {
        string trimmedDevice = volumeDevicePath.TrimEnd('\\');

        using SafeFileHandle handle = Win32StorageInterop.CreateFileW(
            trimmedDevice,
            Win32StorageInterop.GENERIC_READ | Win32StorageInterop.GENERIC_WRITE,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            Win32StorageInterop.FILE_FLAG_WRITE_THROUGH,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"Unable to open volume '{trimmedDevice}' for native FAT32 formatting (Win32 error {err}).");
        }

        void VerifyExpectedExtent()
        {
            if (expectedDiskNumber is not null &&
                (expectedVolumeLengthBytes is null ||
                 !Win32StorageInterop.TryGetSingleVolumeDiskExtent(handle, out int actualDisk, out long actualOffset, out long actualLength) ||
                 actualDisk != expectedDiskNumber || actualOffset != partitionOffsetBytes || actualLength != expectedVolumeLengthBytes))
                throw new InvalidOperationException("The volume opened for formatting no longer matches the verified installer partition.");
        }

        VerifyExpectedExtent();
        int? volumeSectorBytes = Win32StorageInterop.TryQueryLogicalSectorSize(handle, out int actualSectorBytes)
            ? actualSectorBytes : null;
        int bytesPerSector = SelectVerifiedSectorSize(expectedSectorBytes, volumeSectorBytes);

        // Allow raw sector writes across the entire partition and lock/dismount any existing filesystem
        Win32StorageInterop.TryControlCode(handle, Win32StorageInterop.FSCTL_ALLOW_EXTENDED_DASD_IO);

        bool locked = false;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Win32StorageInterop.TryControlCode(handle, Win32StorageInterop.FSCTL_LOCK_VOLUME))
            {
                locked = true;
                break;
            }
            Thread.Sleep(150);
        }

        if (!locked)
            throw new InvalidOperationException("The installer volume could not be locked; it was not formatted.");

        try
        {
            VerifyExpectedExtent();
            Win32StorageInterop.TryControlCode(handle, Win32StorageInterop.FSCTL_DISMOUNT_VOLUME);
            long lengthBytes = Win32StorageInterop.QueryDeviceLengthBytes(handle);
            if (expectedVolumeLengthBytes is not null && lengthBytes != expectedVolumeLengthBytes)
                throw new InvalidOperationException("The installer volume length changed; it was not formatted.");

            using var rawStream = new NonOwningRawDeviceStream(handle, lengthBytes);
            FormatStream(rawStream, lengthBytes, bytesPerSector, volumeLabel, partitionOffsetBytes);
            rawStream.Flush();
        }
        finally
        {
            Win32StorageInterop.FlushFileBuffers(handle);
            // Dismount once more so Windows fastfat.sys mounts the freshly written FAT32 BPB cleanly on next access
            Win32StorageInterop.TryControlCode(handle, Win32StorageInterop.FSCTL_DISMOUNT_VOLUME);
            if (locked)
            {
                Win32StorageInterop.TryControlCode(handle, Win32StorageInterop.FSCTL_UNLOCK_VOLUME);
            }
        }
    }

    internal static int SelectVerifiedSectorSize(int? diskSectorBytes, int? volumeSectorBytes)
    {
        static bool Valid(int? size) => size is 512 or 1024 or 2048 or 4096;
        if ((diskSectorBytes is not null && !Valid(diskSectorBytes)) ||
            (volumeSectorBytes is not null && !Valid(volumeSectorBytes)) ||
            (diskSectorBytes is null && volumeSectorBytes is null) ||
            (diskSectorBytes is not null && volumeSectorBytes is not null && diskSectorBytes != volumeSectorBytes))
            throw new InvalidOperationException("The logical sector size could not be verified; the volume was not formatted.");
        return diskSectorBytes ?? volumeSectorBytes!.Value;
    }

    private sealed class NonOwningRawDeviceStream : Stream
    {
        private readonly SafeFileHandle _handle;
        private readonly long _length;
        private long _position;

        public NonOwningRawDeviceStream(SafeFileHandle handle, long length)
        {
            _handle = handle;
            _length = length;
            if (!Win32StorageInterop.SetFilePointerEx(_handle, 0, out _position, 0))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"SetFilePointerEx(0) failed on raw FAT32 volume (Win32 error {err}).");
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush() => Win32StorageInterop.FlushFileBuffers(_handle);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            uint method = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => 1,
                SeekOrigin.End => 2,
                _ => 0
            };

            if (!Win32StorageInterop.SetFilePointerEx(_handle, offset, out _position, method))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"SetFilePointerEx({offset}, {origin}) failed on raw FAT32 volume (Win32 error {err}).");
            }

            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return;

            int remaining = count;
            int curOffset = offset;
            while (remaining > 0)
            {
                if (!Win32StorageInterop.WriteFile(
                        _handle,
                        ref buffer[curOffset],
                        (uint)remaining,
                        out uint written,
                        IntPtr.Zero) || written == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new Win32Exception(err, $"WriteFile({remaining} bytes at {_position}) failed on raw FAT32 volume (Win32 error {err}).");
                }

                curOffset += (int)written;
                remaining -= (int)written;
                _position += written;
            }
        }
    }

    /// <summary>
    /// Writes a complete, bootable, UEFI- and Windows-Setup-compatible FAT32 filesystem onto any seekable stream.
    /// Raw Win32 volume handles require sector-aligned writes, so all writes are aligned to <paramref name="bytesPerSector"/>.
    /// </summary>
    public static void FormatStream(
        Stream target,
        long volumeSizeBytes,
        int bytesPerSector,
        string volumeLabel,
        long partitionOffsetBytes = 0)
    {
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096))
        {
            bytesPerSector = 512;
        }

        ulong rawTotalSectors = (ulong)volumeSizeBytes / (ulong)bytesPerSector;
        if (rawTotalSectors < 66_000 && bytesPerSector == 512 && volumeSizeBytes < 34L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(volumeSizeBytes), "FAT32 volume must be at least 34 MB.");
        }

        uint totalSectors = (uint)Math.Min(uint.MaxValue, rawTotalSectors);
        byte sectorsPerCluster = ChooseSectorsPerCluster(volumeSizeBytes, bytesPerSector, totalSectors);

        const ushort reservedSectors = 32;
        const byte numFats = 2;

        ulong maxDataSectors = totalSectors - reservedSectors;
        ulong maxClusters = (maxDataSectors / sectorsPerCluster) + 2UL;
        uint fatSizeSectors = (uint)(((maxClusters * 4UL) + (ulong)bytesPerSector - 1UL) / (ulong)bytesPerSector);

        // Align data region start (reservedSectors + 2 * fatSizeSectors) to a multiple of sectorsPerCluster for flash/SSD optimal alignment
        uint remainder = (reservedSectors + (numFats * fatSizeSectors)) % sectorsPerCluster;
        if (remainder != 0)
        {
            uint pad = sectorsPerCluster - remainder;
            if ((pad & 1) == 1 && (sectorsPerCluster & 1) == 0)
            {
                fatSizeSectors += pad / numFats;
            }
        }

        ulong dataSectors = totalSectors - reservedSectors - ((ulong)numFats * fatSizeSectors);
        uint countOfClusters = (uint)(dataSectors / sectorsPerCluster);
        if (countOfClusters < MinFat32ClusterCount)
        {
            throw new InvalidOperationException(
                $"Calculated cluster count ({countOfClusters}) is below FAT32 minimum ({MinFat32ClusterCount}).");
        }

        byte[] cleanLabel11 = FormatFat11ByteLabel(volumeLabel);
        uint serialNumber = GenerateVolumeSerialNumber();
        uint hiddenSectors = (uint)Math.Min(uint.MaxValue, Math.Max(0L, partitionOffsetBytes / bytesPerSector));

        // 1. Build the 32-sector Reserved Region (Boot Sector 0, FSInfo Sector 1, Sector 2 sig, Backup at 6..8)
        byte[] reservedRegion = new byte[reservedSectors * bytesPerSector];

        BuildBootSector(
            reservedRegion.AsSpan(0, bytesPerSector),
            bytesPerSector,
            sectorsPerCluster,
            reservedSectors,
            numFats,
            hiddenSectors,
            totalSectors,
            fatSizeSectors,
            serialNumber,
            cleanLabel11);

        BuildFsInfoSector(
            reservedRegion.AsSpan(1 * bytesPerSector, bytesPerSector),
            freeClusterCount: countOfClusters - 1,
            nextFreeCluster: 3);

        // Sector 2 trailing boot signature (standard Microsoft 3-sector FAT32 boot record)
        reservedRegion[(2 * bytesPerSector) + 510] = 0x55;
        reservedRegion[(2 * bytesPerSector) + 511] = 0xAA;

        // Copy Sectors 0..2 -> Backup Boot Sectors 6..8
        reservedRegion.AsSpan(0, 3 * bytesPerSector)
            .CopyTo(reservedRegion.AsSpan(6 * bytesPerSector, 3 * bytesPerSector));

        target.Position = 0;
        target.Write(reservedRegion, 0, reservedRegion.Length);

        // 2. Write FAT #1 and FAT #2
        long fatBytes = (long)fatSizeSectors * bytesPerSector;
        WriteSingleFatTable(target, bytesPerSector, fatBytes);
        WriteSingleFatTable(target, bytesPerSector, fatBytes);

        // 3. Write Root Directory (Cluster 2) with Volume Label entry
        int clusterBytes = sectorsPerCluster * bytesPerSector;
        byte[] rootCluster = new byte[clusterBytes];
        WriteVolumeLabelDirectoryEntry(rootCluster.AsSpan(0, 32), cleanLabel11);
        target.Write(rootCluster, 0, rootCluster.Length);
    }

    public static byte[] FormatFat11ByteLabel(string? rawLabel)
    {
        byte[] result = Encoding.ASCII.GetBytes("           "); // 11 spaces
        if (string.IsNullOrWhiteSpace(rawLabel))
        {
            Encoding.ASCII.GetBytes("SWBI-SETUP ").CopyTo(result, 0);
            return result;
        }

        int pos = 0;
        foreach (char c in rawLabel.Trim().ToUpperInvariant())
        {
            if (pos >= 11) break;
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c is '-' or '_' or ' ')
            {
                result[pos++] = (byte)c;
            }
        }

        if (pos == 0)
        {
            Encoding.ASCII.GetBytes("SWBI-SETUP ").CopyTo(result, 0);
        }

        return result;
    }

    private static byte ChooseSectorsPerCluster(long volumeBytes, int bytesPerSector, uint totalSectors)
    {
        const long gb = 1024L * 1024L * 1024L;
        int targetClusterBytes = volumeBytes switch
        {
            < 260L * 1024 * 1024 => 512,
            < 8L * gb => 4096,
            < 16L * gb => 8192,
            < 32L * gb => 16384,
            _ => 32768
        };

        int spc = Math.Clamp(targetClusterBytes / bytesPerSector, 1, 128);
        // Step down power-of-two if needed so small volumes still have >= 65,525 clusters
        while (spc > 1 && ((totalSectors - 64) / spc) < (MinFat32ClusterCount + 64))
        {
            spc >>= 1;
        }

        return (byte)spc;
    }

    private static void BuildBootSector(
        Span<byte> sector,
        int bytesPerSector,
        byte sectorsPerCluster,
        ushort reservedSectors,
        byte numFats,
        uint hiddenSectors,
        uint totalSectors,
        uint fatSizeSectors,
        uint serialNumber,
        ReadOnlySpan<byte> label11)
    {
        sector.Clear();
        sector[0] = 0xEB;
        sector[1] = 0x58;
        sector[2] = 0x90;
        "MSWIN4.1"u8.CopyTo(sector.Slice(3, 8));

        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(11, 2), (ushort)bytesPerSector);
        sector[13] = sectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(14, 2), reservedSectors);
        sector[16] = numFats;
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(17, 2), 0); // RootEntCnt = 0
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(19, 2), 0); // TotSec16 = 0
        sector[21] = 0xF8;                                                // Fixed media
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(22, 2), 0); // FATSz16 = 0
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(24, 2), 63);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(26, 2), 255);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(28, 4), hiddenSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(32, 4), totalSectors);

        // FAT32 Extended BPB
        BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(36, 4), fatSizeSectors);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(40, 2), 0); // ExtFlags
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(42, 2), 0); // FSVer 0.0
        BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(44, 4), 2); // RootClus = 2
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(48, 2), 1); // FSInfo = 1
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(50, 2), 6); // BkBootSec = 6

        sector[64] = 0x80; // BS_DrvNum
        sector[65] = 0x00;
        sector[66] = 0x29; // BS_BootSig
        BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(67, 4), serialNumber);
        label11.CopyTo(sector.Slice(71, 11));
        "FAT32   "u8.CopyTo(sector.Slice(82, 8));

        sector[510] = 0x55;
        sector[511] = 0xAA;
    }

    private static void BuildFsInfoSector(Span<byte> sector, uint freeClusterCount, uint nextFreeCluster)
    {
        sector.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(0, 4), 0x41615252u);   // "RRaA"
        BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(484, 4), 0x61417272u); // "rrAa"
        BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(488, 4), freeClusterCount);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(492, 4), nextFreeCluster);
        sector[510] = 0x55;
        sector[511] = 0xAA;
    }

    private static void WriteSingleFatTable(Stream target, int bytesPerSector, long fatBytes)
    {
        int chunkSize = Math.Max(bytesPerSector, 1024 * 1024);
        chunkSize -= chunkSize % bytesPerSector;

        byte[] buffer = new byte[chunkSize];
        // First sector of FAT holds entries 0, 1, and 2 (Root Directory EOC)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0x0FFFFFF8u);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), 0x0FFFFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), 0x0FFFFFFFu);

        int firstWrite = (int)Math.Min(fatBytes, buffer.Length);
        target.Write(buffer, 0, firstWrite);

        // Zero remaining FAT sectors
        buffer.AsSpan(0, 12).Clear();
        long remaining = fatBytes - firstWrite;
        while (remaining > 0)
        {
            int toWrite = (int)Math.Min(remaining, buffer.Length);
            target.Write(buffer, 0, toWrite);
            remaining -= toWrite;
        }
    }

    private static void WriteVolumeLabelDirectoryEntry(Span<byte> entry32, ReadOnlySpan<byte> label11)
    {
        entry32.Clear();
        label11.CopyTo(entry32.Slice(0, 11));
        entry32[11] = 0x08; // ATTR_VOLUME_ID

        DateTime now = DateTime.UtcNow;
        ushort dosDate = (ushort)(((Math.Clamp(now.Year - 1980, 0, 127)) << 9) | (now.Month << 5) | now.Day);
        ushort dosTime = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
        BinaryPrimitives.WriteUInt16LittleEndian(entry32.Slice(22, 2), dosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(entry32.Slice(24, 2), dosDate);
    }

    private static uint GenerateVolumeSerialNumber()
    {
        Span<byte> b = stackalloc byte[4];
        Random.Shared.NextBytes(b);
        uint val = BinaryPrimitives.ReadUInt32LittleEndian(b);
        return val == 0 ? 0x53574249u : val;
    }
}
