using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UsbFreeInstaller.Core.Services.Native;

public sealed record RawPartitionLayoutEntry(
    int PartitionStyle, // 0 = MBR, 1 = GPT
    long StartingOffsetBytes,
    long PartitionLengthBytes,
    int PartitionNumber,
    Guid GptPartitionType,
    Guid GptPartitionId,
    ulong GptAttributes,
    string GptName,
    byte MbrPartitionType,
    bool MbrBootIndicator,
    bool MbrRecognized,
    uint MbrHiddenSectors,
    Guid MbrPartitionId,
    int RawSlotIndex = -1)
{
    public bool IsMbrExtendedContainer =>
        PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR &&
        MbrPartitionType is 0x05 or 0x0F;
}

public sealed record RawDiskLayoutSnapshot(
    int PartitionStyle, // 0 = MBR, 1 = GPT
    Guid GptDiskId,
    long GptStartingUsableOffset,
    long GptUsableLength,
    uint GptMaxPartitionCount,
    uint MbrSignature,
    uint MbrCheckSum,
    IReadOnlyList<RawPartitionLayoutEntry> Partitions,
    int MbrPrimarySlotsUsed = 0,
    bool HasMbrExtendedPartition = false,
    byte[]? RawLayoutBytes = null);

public sealed record RawVolumeBinding(
    string VolumeGuidPath, // e.g. \\?\Volume{guid}\
    string? DriveLetter,   // e.g. "C"
    string Label,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    int DiskNumber,
    int PartitionNumber,
    long StartingOffsetBytes);

public sealed record RawStorageHardwareProps(
    string FriendlyName,
    string SerialNumber,
    string BusTypeName,
    bool IsRemovableMedia,
    bool? IncursSeekPenalty,
    bool? SupportsTrim,
    int LogicalSectorBytes,
    int PhysicalSectorBytes);

/// <summary>
/// Direct Win32 Kernel IOCTL, Volume Management, Firmware NVRAM, Token Privilege, Registry Hive,
/// and System Shutdown interop layer (kernel32.dll, advapi32.dll, tbs.dll).
/// Operates with ZERO external command-line tools (no powershell.exe, diskpart.exe, format.com, bcdedit.exe, reg.exe, or shutdown.exe).
/// </summary>
public static class Win32StorageInterop
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint SYNCHRONIZE = 0x00100000;

    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;

    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    // Storage / Disk / Volume / FSCTL IOCTLs
    public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;
    public const uint IOCTL_DISK_SET_DRIVE_LAYOUT_EX = 0x0007C054;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint IOCTL_DISK_GROW_PARTITION = 0x0007C0D0;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    public const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
    public const uint FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073;
    public const uint FSCTL_MOVE_FILE = 0x00090074;
    public const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;
    public const uint FSCTL_EXTEND_VOLUME = 0x000900F0;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    public const uint FSCTL_DELETE_USN_JOURNAL = 0x000900F8;
    public const uint FSCTL_SHRINK_VOLUME = 0x000901B0;

    // MountPointManager IOCTLs (suppress Explorer "You need to format the disk in drive E:" popup while raw partition is being formatted)
    public const uint IOCTL_MOUNTMGR_QUERY_AUTO_MOUNT = 0x006D003C;
    public const uint IOCTL_MOUNTMGR_SET_AUTO_MOUNT = 0x006D0040;
    public const ulong GPT_BASIC_DATA_ATTRIBUTE_NO_DRIVE_LETTER = 0x8000000000000000UL;

    public const int PARTITION_STYLE_MBR = 0;
    public const int PARTITION_STYLE_GPT = 1;

    public static readonly Guid GptBasicDataTypeGuid = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    public static readonly Guid GptEfiSystemTypeGuid = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid GptMsrTypeGuid = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    public static readonly Guid GptWinReTypeGuid = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");

    public const string EfiGlobalVariableNamespaceGuid = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";
    public const uint EFI_VARIABLE_NON_VOLATILE = 0x00000001;
    public const uint EFI_VARIABLE_BOOTSERVICE_ACCESS = 0x00000002;
    public const uint EFI_VARIABLE_RUNTIME_ACCESS = 0x00000004;
    public const uint EFI_GLOBAL_NV_BS_RT = EFI_VARIABLE_NON_VOLATILE | EFI_VARIABLE_BOOTSERVICE_ACCESS | EFI_VARIABLE_RUNTIME_ACCESS;

    private const int DriveLayoutHeaderBytes = 48;
    private const int PartitionEntryStrideBytes = 144;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        int nInBufferSize,
        byte[]? lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool ReadFile(
        SafeFileHandle hFile,
        [Out] byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool WriteFile(
        SafeFileHandle hFile,
        ref byte lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr FindFirstVolumeW(StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool FindNextVolumeW(IntPtr hFindVolume, StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool FindVolumeClose(IntPtr hFindVolume);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool GetVolumePathNamesForVolumeNameW(
        string lpszVolumeName,
        char[] lpszVolumePathNames,
        uint cchBufferLength,
        out uint lpcchReturnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    public static extern bool SetVolumeMountPointW(string lpszVolumeMountPoint, string lpszVolumeName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    public static extern bool DeleteVolumeMountPointW(string lpszVolumeMountPoint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    public static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    public static extern uint GetFirmwareEnvironmentVariableExW(
        string lpName,
        string lpGuid,
        byte[]? pBuffer,
        uint nSize,
        out uint pdwAttribubutes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    public static extern bool SetFirmwareEnvironmentVariableExW(
        string lpName,
        string lpGuid,
        byte[]? pValue,
        uint nSize,
        uint dwAttributes);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern uint GetSystemFirmwareTable(
        uint FirmwareTableProviderSignature,
        uint FirmwareTableID,
        IntPtr pFirmwareTableBuffer,
        uint BufferSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint TbsiGetDeviceInfoDelegate(uint Size, out TBS_DEVICE_INFO Info);

    /// <summary>
    /// Detects TPM 2.0 / 1.2 directly from motherboard ACPI firmware tables ('TPM2' / 'TCPA') via kernel32.dll
    /// + dynamic tbs.dll probe — never fails when tbs.dll or TPM Registry keys are stripped on modded Windows.
    /// </summary>
    public static bool TryDetectTpmHardware(out string tpmVersion)
    {
        tpmVersion = "None";
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // Priority 1: Direct motherboard ACPI 'TPM2' table (0x41435049 = 'ACPI', 0x324D5054 = 'TPM2' little-endian)
        try
        {
            const uint ACPI_SIGNATURE = 0x41435049; // 'ACPI'
            const uint TPM2_TABLE_ID = 0x324D5054;  // 'TPM2'
            const uint TCPA_TABLE_ID = 0x41504354;  // 'TCPA' (TPM 1.2)

            if (GetSystemFirmwareTable(ACPI_SIGNATURE, TPM2_TABLE_ID, IntPtr.Zero, 0) > 0)
            {
                tpmVersion = "2.0";
                return true;
            }

            if (GetSystemFirmwareTable(ACPI_SIGNATURE, TCPA_TABLE_ID, IntPtr.Zero, 0) > 0)
            {
                tpmVersion = "1.2";
                return true;
            }
        }
        catch
        {
            // Continue to dynamic TBS check
        }

        // Priority 2: Dynamic NativeLibrary.TryLoad("tbs.dll") — zero static DllImport failure if tbs.dll was deleted
        try
        {
            string tbsPath = Path.Combine(Environment.SystemDirectory, "tbs.dll");
            if (File.Exists(tbsPath) &&
                NativeLibrary.TryLoad(tbsPath, out IntPtr hTbs) &&
                hTbs != IntPtr.Zero &&
                NativeLibrary.TryGetExport(hTbs, "Tbsi_GetDeviceInfo", out IntPtr pFn))
            {
                var fn = Marshal.GetDelegateForFunctionPointer<TbsiGetDeviceInfoDelegate>(pFn);
                uint infoSize = (uint)Marshal.SizeOf<TBS_DEVICE_INFO>();
                if (fn(infoSize, out TBS_DEVICE_INFO info) == 0 && info.tpmVersion > 0)
                {
                    tpmVersion = info.tpmVersion >= 2 ? "2.0" : "1.2";
                    return true;
                }
            }
        }
        catch
        {
            // Ignore stripped tbs.dll
        }

        return false;
    }

    // Token privilege & Registry Hive & Shutdown APIs (advapi32.dll)
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        int BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static readonly IntPtr HKEY_LOCAL_MACHINE_PTR = new(unchecked((int)0x80000002));
    public const uint REG_OPTION_BACKUP_RESTORE = 0x00000004;
    public const uint KEY_READ_ACCESS = 0x00020019;
    public const uint KEY_ALL_ACCESS_MASK = 0x000F003F;
    public const uint REG_SZ = 1;
    public const uint REG_BINARY = 3;
    public const uint REG_DWORD = 4;
    public const uint REG_MULTI_SZ = 7;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int RegLoadKeyW(IntPtr hKey, string lpSubKey, string lpFile);

    [DllImport("advapi32.dll", EntryPoint = "RegUnLoadKeyW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int RegUnloadKeyW(IntPtr hKey, string lpSubKey);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int RegCreateKeyExW(
        IntPtr hKey,
        string lpSubKey,
        uint Reserved,
        string? lpClass,
        uint dwOptions,
        uint samDesired,
        IntPtr lpSecurityAttributes,
        out IntPtr phkResult,
        out uint lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int RegSetValueExW(
        IntPtr hKey,
        string? lpValueName,
        uint Reserved,
        uint dwType,
        byte[] lpData,
        uint cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int RegQueryValueExW(
        IntPtr hKey,
        string? lpValueName,
        IntPtr lpReserved,
        out uint lpType,
        byte[]? lpData,
        ref uint lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int RegDeleteValueW(IntPtr hKey, string? lpValueName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int RegDeleteTreeW(IntPtr hKey, string? lpSubKey);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    public static extern int NtDeleteKey(IntPtr KeyHandle);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    public static extern int RegFlushKey(IntPtr hKey);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int RegSaveKeyExW(IntPtr hKey, string lpFile, IntPtr lpSecurityAttributes, uint Flags);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    public static extern int RegCloseKey(IntPtr hKey);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    public static extern bool InitiateSystemShutdownExW(
        string? lpMachineName,
        string? lpMessage,
        uint dwTimeout,
        bool bForceAppsClosed,
        bool bRebootAfterShutdown,
        uint dwReason);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TBS_DEVICE_INFO
    {
        public uint structVersion;
        public uint tpmVersion; // 1 = TPM 1.2, 2 = TPM 2.0
        public uint tpmInterfaceType;
        public uint tpmImpRevision;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    public static bool EnableProcessPrivilege(string privilegeName)
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        const uint TOKEN_QUERY = 0x0008;
        const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, privilegeName, out LUID luid))
            {
                return false;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };

            bool adjusted = AdjustTokenPrivileges(token, false, ref tp, Marshal.SizeOf<TOKEN_PRIVILEGES>(), IntPtr.Zero, IntPtr.Zero);
            // AdjustTokenPrivileges can succeed while reporting that the token lacks the privilege.
            const int ErrorNotAllAssigned = 1300;
            return adjusted && Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public static bool TryControlCode(SafeFileHandle handle, uint controlCode) =>
        DeviceIoControl(handle, controlCode, null, 0, null, 0, out _, IntPtr.Zero);

    public static long QueryDeviceLengthBytes(SafeFileHandle handle)
    {
        byte[] outBuf = new byte[8];
        if (!DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO, null, 0, outBuf, outBuf.Length, out int ret, IntPtr.Zero) || ret < 8)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"IOCTL_DISK_GET_LENGTH_INFO failed (Win32 error {err}).");
        }

        return BinaryPrimitives.ReadInt64LittleEndian(outBuf);
    }

    public static long TryQueryDeviceLengthBytesOrZero(SafeFileHandle handle)
    {
        byte[] outBuf = new byte[8];
        if (DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO, null, 0, outBuf, outBuf.Length, out int ret, IntPtr.Zero) && ret >= 8)
        {
            return BinaryPrimitives.ReadInt64LittleEndian(outBuf);
        }

        return 0;
    }

    public static int QueryLogicalSectorSize(SafeFileHandle handle) =>
        TryQueryLogicalSectorSize(handle, out int bytesPerSector) ? bytesPerSector : 512;

    public static bool TryQueryLogicalSectorSize(SafeFileHandle handle, out int bytesPerSector)
    {
        bytesPerSector = 0;
        byte[] outBuf = new byte[32]; // DISK_GEOMETRY (24 bytes)
        if (DeviceIoControl(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY, null, 0, outBuf, outBuf.Length, out int ret, IntPtr.Zero) && ret >= 24)
        {
            int bps = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(20, 4));
            if (bps is 512 or 1024 or 2048 or 4096)
            {
                bytesPerSector = bps;
                return true;
            }
        }
        return false;
    }

    public static bool TryGetDeviceNumbers(SafeFileHandle volumeHandle, out int diskNumber, out int partitionNumber)
    {
        diskNumber = -1;
        partitionNumber = -1;

        byte[] outBuf = new byte[16]; // STORAGE_DEVICE_NUMBER (12 bytes)
        if (DeviceIoControl(volumeHandle, IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, outBuf, outBuf.Length, out int ret, IntPtr.Zero) && ret >= 12)
        {
            diskNumber = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(4, 4));
            partitionNumber = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(8, 4));
            return diskNumber >= 0;
        }

        return false;
    }

    public static bool TryGetVolumeFirstDiskExtent(SafeFileHandle volumeHandle, out int diskNumber, out long startingOffsetBytes, out long extentLengthBytes)
    {
        diskNumber = -1;
        startingOffsetBytes = 0;
        extentLengthBytes = 0;

        byte[] outBuf = new byte[64]; // VOLUME_DISK_EXTENTS (8 header + 24 first DISK_EXTENT = 32 bytes)
        if (DeviceIoControl(volumeHandle, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, outBuf, outBuf.Length, out int ret, IntPtr.Zero) && ret >= 32)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(0, 4));
            if (count >= 1)
            {
                diskNumber = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(8, 4));
                startingOffsetBytes = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(16, 8));
                extentLengthBytes = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(24, 8));
                return diskNumber >= 0;
            }
        }

        return false;
    }

    public static bool TryGetSingleVolumeDiskExtent(SafeFileHandle volumeHandle, out int diskNumber, out long startingOffsetBytes) =>
        TryGetSingleVolumeDiskExtent(volumeHandle, out diskNumber, out startingOffsetBytes, out _);

    public static bool TryGetSingleVolumeDiskExtent(SafeFileHandle volumeHandle, out int diskNumber,
        out long startingOffsetBytes, out long extentLengthBytes)
    {
        diskNumber = -1;
        startingOffsetBytes = 0;
        extentLengthBytes = 0;
        byte[] output = new byte[64];
        if (!DeviceIoControl(volumeHandle, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0,
                output, output.Length, out int returned, IntPtr.Zero) || returned < 32 ||
            BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(0, 4)) != 1)
        {
            return false;
        }

        diskNumber = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8, 4));
        startingOffsetBytes = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16, 8));
        extentLengthBytes = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24, 8));
        return diskNumber >= 0 && startingOffsetBytes >= 0 && extentLengthBytes > 0;
    }

    public static IReadOnlyList<RawVolumeBinding> EnumerateAllVolumes()
    {
        var result = new List<RawVolumeBinding>();
        var volNameBuf = new StringBuilder(512);
        IntPtr findHandle = FindFirstVolumeW(volNameBuf, (uint)volNameBuf.Capacity);
        if (findHandle == IntPtr.Zero || findHandle == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            do
            {
                string volGuidPath = volNameBuf.ToString(); // e.g. "\\?\Volume{...}\"
                string? driveLetter = TryGetDriveLetterForVolume(volGuidPath);

                string label = string.Empty;
                string fileSystem = string.Empty;
                var labelSb = new StringBuilder(260);
                var fsSb = new StringBuilder(64);
                if (GetVolumeInformationW(volGuidPath, labelSb, (uint)labelSb.Capacity, out _, out _, out _, fsSb, (uint)fsSb.Capacity))
                {
                    label = labelSb.ToString();
                    fileSystem = fsSb.ToString();
                }

                long totalBytes = 0;
                long freeBytes = 0;
                if (GetDiskFreeSpaceExW(volGuidPath, out ulong avail, out ulong total, out _))
                {
                    totalBytes = (long)total;
                    freeBytes = (long)avail;
                }

                int diskNum = -1;
                int partNum = -1;
                long startOff = -1;
                string deviceOpenPath = volGuidPath.TrimEnd('\\');
                using SafeFileHandle vh = CreateFileW(
                    deviceOpenPath,
                    0,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (!vh.IsInvalid)
                {
                    TryGetDeviceNumbers(vh, out diskNum, out partNum);
                    if (TryGetVolumeFirstDiskExtent(vh, out int extDisk, out long extOff, out _))
                    {
                        if (diskNum < 0) diskNum = extDisk;
                        startOff = extOff;
                    }
                }

                result.Add(new RawVolumeBinding(
                    volGuidPath,
                    driveLetter,
                    label,
                    fileSystem,
                    totalBytes,
                    freeBytes,
                    diskNum,
                    partNum,
                    startOff));

                volNameBuf.Clear();
            } while (FindNextVolumeW(findHandle, volNameBuf, (uint)volNameBuf.Capacity));
        }
        finally
        {
            FindVolumeClose(findHandle);
        }

        return result;
    }

    public static RawDiskLayoutSnapshot? TryReadDiskLayout(SafeFileHandle diskHandle)
    {
        // Up to 128 GPT partitions: 48 + 128 * 144 = 18,480 bytes; allocate 32 KB safely
        byte[] buf = new byte[32 * 1024];
        if (!DeviceIoControl(diskHandle, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, null, 0, buf, buf.Length, out int ret, IntPtr.Zero) ||
            ret < DriveLayoutHeaderBytes)
        {
            return null;
        }

        return ParseDriveLayoutBuffer(buf, ret);
    }

    public static RawDiskLayoutSnapshot? ParseDriveLayoutBuffer(byte[] buf, int ret)
    {
        if (buf is null || ret < DriveLayoutHeaderBytes || ret > buf.Length)
        {
            return null;
        }

        int style = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        int count = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));

        Guid gptDiskId = Guid.Empty;
        long gptStartUsable = 0;
        long gptUsableLen = 0;
        uint gptMaxParts = 128;
        uint mbrSig = 0;
        uint mbrCsum = 0;

        if (style == PARTITION_STYLE_GPT)
        {
            gptDiskId = new Guid(buf.AsSpan(8, 16));
            gptStartUsable = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(24, 8));
            gptUsableLen = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(32, 8));
            gptMaxParts = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(40, 4));
        }
        else if (style == PARTITION_STYLE_MBR)
        {
            mbrSig = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8, 4));
            mbrCsum = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12, 4));
        }

        var parts = new List<RawPartitionLayoutEntry>();
        int maxEntriesInBuf = (ret - DriveLayoutHeaderBytes) / PartitionEntryStrideBytes;
        int safeCount = Math.Min(count, maxEntriesInBuf);
        int mbrPrimaryUsed = 0;
        bool hasMbrExtended = false;

        for (int i = 0; i < safeCount; i++)
        {
            ReadOnlySpan<byte> entry = buf.AsSpan(DriveLayoutHeaderBytes + (i * PartitionEntryStrideBytes), PartitionEntryStrideBytes);
            int pStyle = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(0, 4));
            long startOff = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(8, 8));
            long partLen = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(16, 8));
            int partNum = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(24, 4));

            if (partLen <= 0)
            {
                continue;
            }

            if (pStyle == PARTITION_STYLE_MBR && entry[32] == 0)
            {
                // Unused MBR table slot
                continue;
            }

            if (pStyle == PARTITION_STYLE_GPT)
            {
                Guid pType = new(entry.Slice(32, 16));
                if (pType == Guid.Empty)
                {
                    continue;
                }

                Guid pId = new(entry.Slice(48, 16));
                ulong attrs = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(64, 8));
                string pName = Encoding.Unicode.GetString(entry.Slice(72, 72)).TrimEnd('\0');

                parts.Add(new RawPartitionLayoutEntry(
                    pStyle, startOff, partLen, partNum,
                    pType, pId, attrs, pName,
                    0, false, false, 0, Guid.Empty, i));
            }
            else
            {
                byte mbrType = entry[32];
                bool bootInd = entry[33] != 0;
                bool recog = entry[34] != 0;
                uint hidden = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(36, 4));
                Guid mbrGuid = new(entry.Slice(40, 16));

                if (i < 4)
                {
                    mbrPrimaryUsed++;
                    if (mbrType is 0x05 or 0x0F)
                    {
                        hasMbrExtended = true;
                    }
                }

                parts.Add(new RawPartitionLayoutEntry(
                    pStyle, startOff, partLen, partNum,
                    Guid.Empty, Guid.Empty, 0, string.Empty,
                    mbrType, bootInd, recog, hidden, mbrGuid, i));
            }
        }

        byte[] rawSnapshotBytes = new byte[DriveLayoutHeaderBytes + (safeCount * PartitionEntryStrideBytes)];
        Buffer.BlockCopy(buf, 0, rawSnapshotBytes, 0, rawSnapshotBytes.Length);

        return new RawDiskLayoutSnapshot(
            style,
            gptDiskId,
            gptStartUsable,
            gptUsableLen,
            gptMaxParts,
            mbrSig,
            mbrCsum,
            parts.OrderBy(p => p.StartingOffsetBytes).ToArray(),
            mbrPrimaryUsed,
            hasMbrExtended,
            rawSnapshotBytes);
    }

    /// <summary>
    /// Mutates a single primary MBR slot (0..3) in-place inside a cloned DRIVE_LAYOUT_INFORMATION_EX buffer,
    /// preserving all existing primary slots, Active (0x80) flags, AND any Extended/Logical EBR slots (4..N) byte-for-byte.
    /// </summary>
    public static byte[]? TrySerializeMbrSlotPatchPreservingEbrs(
        RawDiskLayoutSnapshot baseLayout,
        RawPartitionLayoutEntry? newPrimaryPartitionToAdd,
        int? primarySlotIndexToDelete)
    {
        if (baseLayout.PartitionStyle != PARTITION_STYLE_MBR ||
            baseLayout.RawLayoutBytes is not { Length: >= DriveLayoutHeaderBytes + (4 * PartitionEntryStrideBytes) } src)
        {
            return null;
        }

        byte[] clone = (byte[])src.Clone();
        int entryCount = (clone.Length - DriveLayoutHeaderBytes) / PartitionEntryStrideBytes;

        // Clear RewritePartition on all untouched slots first so partmgr.sys leaves existing primary & logical partitions untouched
        for (int i = 0; i < entryCount; i++)
        {
            clone[DriveLayoutHeaderBytes + (i * PartitionEntryStrideBytes) + 28] = 0;
        }

        if (primarySlotIndexToDelete is >= 0 and < 4)
        {
            Span<byte> delSlot = clone.AsSpan(DriveLayoutHeaderBytes + (primarySlotIndexToDelete.Value * PartitionEntryStrideBytes), PartitionEntryStrideBytes);
            delSlot.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(delSlot.Slice(0, 4), PARTITION_STYLE_MBR);
            delSlot[28] = 1; // RewritePartition = TRUE
            delSlot[32] = 0; // PARTITION_ENTRY_UNUSED
            return clone;
        }

        if (newPrimaryPartitionToAdd is not null)
        {
            int freeSlot = -1;
            for (int i = 0; i < 4; i++)
            {
                Span<byte> cand = clone.AsSpan(DriveLayoutHeaderBytes + (i * PartitionEntryStrideBytes), PartitionEntryStrideBytes);
                long len = BinaryPrimitives.ReadInt64LittleEndian(cand.Slice(16, 8));
                byte type = cand[32];
                if (len <= 0 || type == 0)
                {
                    freeSlot = i;
                    break;
                }
            }

            if (freeSlot < 0)
            {
                return null;
            }

            Span<byte> slot = clone.AsSpan(DriveLayoutHeaderBytes + (freeSlot * PartitionEntryStrideBytes), PartitionEntryStrideBytes);
            slot.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(0, 4), PARTITION_STYLE_MBR);
            BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(8, 8), newPrimaryPartitionToAdd.StartingOffsetBytes);
            BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(16, 8), newPrimaryPartitionToAdd.PartitionLengthBytes);
            BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(24, 4), 0); // partmgr assigns canonical 1-based number
            slot[28] = 1; // RewritePartition = TRUE
            slot[32] = newPrimaryPartitionToAdd.MbrPartitionType;
            slot[33] = newPrimaryPartitionToAdd.MbrBootIndicator ? (byte)1 : (byte)0;
            slot[34] = newPrimaryPartitionToAdd.MbrRecognized ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(36, 4), newPrimaryPartitionToAdd.MbrHiddenSectors);
            newPrimaryPartitionToAdd.MbrPartitionId.TryWriteBytes(slot.Slice(40, 16));
            return clone;
        }

        return null;
    }

    public static byte[] SerializeDiskLayout(
        RawDiskLayoutSnapshot baseLayout,
        IReadOnlyList<(RawPartitionLayoutEntry Entry, bool Rewrite)> entries)
    {
        int totalCount = baseLayout.PartitionStyle == PARTITION_STYLE_MBR
            ? Math.Max(4, ((entries.Count + 3) / 4) * 4)
            : entries.Count;

        byte[] buf = new byte[DriveLayoutHeaderBytes + (totalCount * PartitionEntryStrideBytes)];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), baseLayout.PartitionStyle);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), totalCount);

        if (baseLayout.PartitionStyle == PARTITION_STYLE_GPT)
        {
            baseLayout.GptDiskId.TryWriteBytes(buf.AsSpan(8, 16));
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24, 8), baseLayout.GptStartingUsableOffset);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(32, 8), baseLayout.GptUsableLength);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(40, 4), Math.Max(128u, baseLayout.GptMaxPartitionCount));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), baseLayout.MbrSignature);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), baseLayout.MbrCheckSum);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            (RawPartitionLayoutEntry e, bool rewrite) = entries[i];
            Span<byte> slot = buf.AsSpan(DriveLayoutHeaderBytes + (i * PartitionEntryStrideBytes), PartitionEntryStrideBytes);
            BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(0, 4), e.PartitionStyle);
            BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(8, 8), e.StartingOffsetBytes);
            BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(16, 8), e.PartitionLengthBytes);
            BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(24, 4), e.PartitionNumber);
            slot[28] = rewrite ? (byte)1 : (byte)0;

            if (e.PartitionStyle == PARTITION_STYLE_GPT)
            {
                e.GptPartitionType.TryWriteBytes(slot.Slice(32, 16));
                e.GptPartitionId.TryWriteBytes(slot.Slice(48, 16));
                BinaryPrimitives.WriteUInt64LittleEndian(slot.Slice(64, 8), e.GptAttributes);

                string truncated = e.GptName.Length > 35 ? e.GptName[..35] : e.GptName;
                Encoding.Unicode.GetBytes(truncated, slot.Slice(72, 72));
            }
            else
            {
                slot[32] = e.MbrPartitionType;
                slot[33] = e.MbrBootIndicator ? (byte)1 : (byte)0;
                slot[34] = e.MbrRecognized ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(36, 4), e.MbrHiddenSectors);
                e.MbrPartitionId.TryWriteBytes(slot.Slice(40, 16));
            }
        }

        return buf;
    }

    public static RawStorageHardwareProps QueryStorageHardwareProperties(SafeFileHandle diskHandle, int diskNumber)
    {
        string friendlyName = $"Physical Disk {diskNumber}";
        string serial = string.Empty;
        string busTypeName = "SATA";
        bool isRemovable = false;

        // 1. StorageDeviceProperty (PropertyId = 0, QueryStandardQuery = 0)
        byte[] query = new byte[12];
        byte[] outBuf = new byte[1024];
        if (DeviceIoControl(diskHandle, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, outBuf, outBuf.Length, out int ret, IntPtr.Zero) && ret >= 32)
        {
            isRemovable = outBuf[8] != 0;
            int vendorOff = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(12, 4));
            int prodOff = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(16, 4));
            int serialOff = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(24, 4));
            byte busType = outBuf[28];

            string vendor = ReadAsciiNullTerm(outBuf, vendorOff, ret);
            string product = ReadAsciiNullTerm(outBuf, prodOff, ret);
            string combined = $"{vendor} {product}".Trim();
            if (!string.IsNullOrWhiteSpace(combined))
            {
                friendlyName = combined;
            }

            serial = ReadAsciiNullTerm(outBuf, serialOff, ret);
            busTypeName = MapStorageBusType(busType);
        }

        // 2. StorageDeviceSeekPenaltyProperty (PropertyId = 7) -> false = SSD/NVMe, true = HDD
        bool? incursSeekPenalty = null;
        BinaryPrimitives.WriteInt32LittleEndian(query.AsSpan(0, 4), 7);
        if (DeviceIoControl(diskHandle, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, outBuf, 32, out ret, IntPtr.Zero) && ret >= 9)
        {
            incursSeekPenalty = outBuf[8] != 0;
        }

        // 3. StorageDeviceTrimProperty (PropertyId = 8)
        bool? supportsTrim = null;
        BinaryPrimitives.WriteInt32LittleEndian(query.AsSpan(0, 4), 8);
        if (DeviceIoControl(diskHandle, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, outBuf, 32, out ret, IntPtr.Zero) && ret >= 9)
        {
            supportsTrim = outBuf[8] != 0;
        }

        // 4. StorageAccessAlignmentProperty (PropertyId = 6)
        int logSector = QueryLogicalSectorSize(diskHandle);
        int physSector = Math.Max(logSector, 4096);
        BinaryPrimitives.WriteInt32LittleEndian(query.AsSpan(0, 4), 6);
        if (DeviceIoControl(diskHandle, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, outBuf, 64, out ret, IntPtr.Zero) && ret >= 28)
        {
            int ls = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(16, 4));
            int ps = BinaryPrimitives.ReadInt32LittleEndian(outBuf.AsSpan(20, 4));
            if (ls is 512 or 4096) logSector = ls;
            if (ps is 512 or 4096 or 8192) physSector = ps;
        }

        return new RawStorageHardwareProps(
            friendlyName,
            serial,
            busTypeName,
            isRemovable,
            incursSeekPenalty,
            supportsTrim,
            logSector,
            physSector);
    }

    public static string? FindFreeDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        char[] preferred = { 'W', 'V', 'U', 'T', 'S', 'R', 'Q', 'P', 'O', 'N', 'M', 'L', 'K', 'J', 'I', 'H', 'Z', 'Y', 'X' };
        foreach (char c in preferred)
        {
            if (!used.Contains(c))
            {
                return c.ToString();
            }
        }

        return null;
    }

    public static IReadOnlyList<string> GetVolumeMountPoints(string volGuidPath)
    {
        var mounts = new List<string>();
        char[] chars = new char[512];
        if (!GetVolumePathNamesForVolumeNameW(volGuidPath, chars, (uint)chars.Length, out uint retLen) || retLen == 0)
        {
            return mounts;
        }

        int idx = 0;
        while (idx < retLen && chars[idx] != '\0')
        {
            int end = idx;
            while (end < retLen && chars[end] != '\0') end++;
            mounts.Add(new string(chars, idx, end - idx));
            idx = end + 1;
        }

        return mounts;
    }

    private static string? TryGetDriveLetterForVolume(string volGuidPath)
    {
        char[] chars = new char[512];
        if (!GetVolumePathNamesForVolumeNameW(volGuidPath, chars, (uint)chars.Length, out uint retLen) || retLen == 0)
        {
            return null;
        }

        int idx = 0;
        while (idx < retLen && chars[idx] != '\0')
        {
            int end = idx;
            while (end < retLen && chars[end] != '\0') end++;
            string mountPath = new(chars, idx, end - idx);
            if (mountPath.Length is 2 or 3 && char.IsLetter(mountPath[0]) && mountPath[1] == ':')
            {
                return char.ToUpperInvariant(mountPath[0]).ToString();
            }
            idx = end + 1;
        }

        return null;
    }

    private static string ReadAsciiNullTerm(byte[] buf, int offset, int maxLen)
    {
        if (offset <= 0 || offset >= maxLen)
        {
            return string.Empty;
        }

        int end = offset;
        while (end < maxLen && buf[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
    }

    private static string MapStorageBusType(byte busType) =>
        busType switch
        {
            7 => "USB",
            8 => "RAID",
            11 => "SATA",
            12 or 13 => "MMC",
            14 or 15 => "FileBackedVirtual",
            17 => "NVMe",
            19 => "UFS",
            10 => "SAS",
            1 => "SCSI",
            _ => "SATA"
        };
}
