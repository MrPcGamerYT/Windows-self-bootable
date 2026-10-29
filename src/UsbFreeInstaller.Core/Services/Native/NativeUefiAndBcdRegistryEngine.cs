using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Services.Native;

/// <summary>
/// Self-contained native Windows Boot Manager (HKLM\BCD00000000 Registry Hive) AND
/// UEFI Firmware NVRAM (BootXXXX + BootOrder + BootNext via SetFirmwareEnvironmentVariableExW)
/// provisioning, inspection, rollback, and system-shutdown engine.
/// Operates with ZERO dependence on bcdedit.exe, powershell.exe, reg.exe, or shutdown.exe.
/// </summary>
public static class NativeUefiAndBcdRegistryEngine
{
    public const string BootMgrObjectGuid = "{9dea862c-5cdd-4e70-acc1-f32b344d4795}";
    public const string FwBootMgrObjectGuid = "{a5a30fa2-3d06-4e9f-b5f4-a01df9d1fcba}";

    private const uint KEY_READ = 0x20019;
    private const uint REG_LATEST_FORMAT = 2;

    // BCD Element Type Hex Keys
    private const string ElemDevice = "11000001";
    private const string ElemPath = "12000002";
    private const string ElemDescription = "12000004";
    private const string ElemOsDevice = "21000001";
    private const string ElemSystemRoot = "22000002";
    private const string ElemDisplayOrder = "24000001";
    private const string ElemBootSequence = "24000002";
    private const string ElemNx = "25000020";
    private const string ElemDetectHal = "26000010";
    private const string ElemWinPe = "26000022";
    private const string ElemRamdiskSdiDevice = "31000003";
    private const string ElemRamdiskSdiPath = "32000004";

    private const int BcdTypeOsLoader = 0x10200003;
    private const int BcdTypeDeviceObject = 0x30000000;

    /// <summary>
    /// Exports a binary backup of HKLM\BCD00000000 using native advapi32 RegSaveKeyExW (zero bcdedit.exe).
    /// </summary>
    public static bool TryExportSystemBcdBackup(string backupFilePath, ISafeLogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            string? dir = Path.GetDirectoryName(backupFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            SwbiWorkDirectory.TryDeleteFile(backupFilePath);
            Win32StorageInterop.EnableProcessPrivilege("SeBackupPrivilege");

            int openErr = Win32StorageInterop.RegOpenKeyExW(
                Win32StorageInterop.HKEY_LOCAL_MACHINE_PTR,
                "BCD00000000",
                Win32StorageInterop.REG_OPTION_BACKUP_RESTORE,
                KEY_READ,
                out IntPtr hKey);
            if (openErr != 0 || hKey == IntPtr.Zero)
            {
                // An arbitrary on-disk BCD is not necessarily the active system store.
                logger.Warn("NativeBCD", $"Cannot open the active system BCD for backup (Win32 {openErr}).");
                return false;
            }

            try
            {
                int saveErr = Win32StorageInterop.RegSaveKeyExW(hKey, backupFilePath, IntPtr.Zero, REG_LATEST_FORMAT);
                if (saveErr == 0 && File.Exists(backupFilePath) && new FileInfo(backupFilePath).Length > 0)
                {
                    logger.Info("NativeBCD", $"Saved native binary BCD hive backup to '{backupFilePath}'.");
                    return true;
                }
            }
            finally
            {
                Win32StorageInterop.RegCloseKey(hKey);
            }
        }
        catch (Exception ex)
        {
            logger.Warn("NativeBCD", $"Native BCD hive backup warning: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Inspects HKLM\BCD00000000 directly via the Windows Registry without spawning bcdedit.exe.
    /// </summary>
    public static ActiveBcdState InspectSystemBcdFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ActiveBcdState(false, string.Empty, string.Empty, null, null, null);
        }

        string? loaderGuid = null;
        string? sdiGuid = null;
        string? fwGuid = null;
        bool currentIsRamdisk = false;
        string devDesc = "partition=C:";

        try
        {
            using SystemBcdObjectsScope? scope = SystemBcdObjectsScope.TryOpen(writable: false);
            RegistryKey? objectsKey = scope?.ObjectsKey;
            if (objectsKey is not null)
            {
                foreach (string subName in objectsKey.GetSubKeyNames())
                {
                    using RegistryKey? objKey = objectsKey.OpenSubKey(subName);
                    if (objKey is null) continue;

                    using RegistryKey? descKey = objKey.OpenSubKey(@"Description");
                    int objType = descKey?.GetValue("Type") is int t ? t : 0;

                    using RegistryKey? titleElem = objKey.OpenSubKey($@"Elements\{ElemDescription}");
                    string title = titleElem?.GetValue("Element") as string ?? string.Empty;

                    bool isSwbiEntry =
                        title.Contains("SWBI", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("Self Windows Bootable Installer", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("USB-Free Windows Setup", StringComparison.OrdinalIgnoreCase);

                    if (!isSwbiEntry)
                    {
                        continue;
                    }

                    if (objType == BcdTypeDeviceObject)
                    {
                        sdiGuid ??= subName;
                    }
                    else if (objType == BcdTypeOsLoader)
                    {
                        loaderGuid ??= subName;
                    }
                    else
                    {
                        fwGuid ??= subName;
                    }
                }
            }

            // Detect if currently booted inside WinPE (MiniNT registry key is always present in WinPE ramdisk boot)
            using RegistryKey? miniNt = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
            if (miniNt is not null)
            {
                currentIsRamdisk = true;
                devDesc = "ramdisk=[boot]\\sources\\boot.wim";
            }
        }
        catch
        {
            // Ignore read-only inspection errors
        }

        // Also check UEFI NVRAM for an active SWBI BootXXXX slot
        if (fwGuid is null && TryFindExistingSwbiUefiBootSlot(out ushort slot))
        {
            fwGuid = $"UEFI:Boot{slot:X4}";
        }

        return new ActiveBcdState(currentIsRamdisk, devDesc, devDesc, loaderGuid, sdiGuid, fwGuid);
    }

    /// <summary>
    /// Provisions the legacy BIOS boot entry in the system BCD without invoking bcdedit.exe.
    /// UEFI uses the standalone installer boot path and must not call this method.
    /// </summary>
    public static (string SdiGuid, string LoaderGuid) ProvisionSystemBcdInRegistry(
        string installerDriveLetter,
        FirmwareMode firmwareMode,
        ISafeLogger logger)
    {
        if (firmwareMode != FirmwareMode.LegacyBios)
        {
            throw new InvalidOperationException("System BCD provisioning is restricted to legacy BIOS; UEFI uses only firmware BootNext.");
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native BCD Registry provisioning requires Windows.");
        }

        string cleanLetter = installerDriveLetter.TrimEnd(':', '\\', '/').ToUpperInvariant();
        Win32StorageInterop.EnableProcessPrivilege("SeRestorePrivilege");
        Win32StorageInterop.EnableProcessPrivilege("SeBackupPrivilege");

        // Resolve exact partition geometry & GUID/offset for SWBI-SETUP
        (RawDiskLayoutSnapshot diskLayout, RawPartitionLayoutEntry swbiPart) = ResolvePartitionOnDisk(cleanLetter);

        string sdiGuid = $"{{{Guid.NewGuid():D}}}";
        string loaderGuid = $"{{{Guid.NewGuid():D}}}";

        byte[] sdiQualifiedPartBlob = BuildBcdQualifiedPartitionElement(Guid.Empty, diskLayout, swbiPart);
        byte[] ramdiskDeviceBlob = BuildBcdRamdiskDeviceElement(
            Guid.Parse(sdiGuid),
            diskLayout,
            swbiPart,
            @"\sources\boot.wim");

        string winloadPath = firmwareMode == FirmwareMode.LegacyBios
            ? @"\Windows\system32\boot\winload.exe"
            : @"\Windows\system32\boot\winload.efi";

        using SystemBcdObjectsScope scope = SystemBcdObjectsScope.TryOpen(writable: true)
            ?? throw new InvalidOperationException(@"The active system BCD hive is not available at HKLM\BCD00000000.");
        IntPtr hObjects = scope.ObjectsHandle;

        // Never overwrite someone else's pending one-time Windows boot.
        int pendingStatus = Win32StorageInterop.RegOpenKeyExW(hObjects,
            $@"{BootMgrObjectGuid}\Elements\{ElemBootSequence}",
            Win32StorageInterop.REG_OPTION_BACKUP_RESTORE, KEY_READ, out IntPtr pending);
        if (pendingStatus == 0)
        {
            Win32StorageInterop.RegCloseKey(pending);
            throw new InvalidOperationException("A Windows Boot Manager one-time sequence already exists; refusing to overwrite it.");
        }
        if (pendingStatus != 2) // ERROR_FILE_NOT_FOUND
        {
            throw new System.ComponentModel.Win32Exception(pendingStatus, "Cannot inspect the existing Windows boot sequence.");
        }

        try
        {
            // 1. Create dedicated Ramdisk SDI Device Object (Type 0x30000000) via REG_OPTION_BACKUP_RESTORE (bypasses SYSTEM-only DACL)
            SetBcdDwordValue(hObjects, $@"{sdiGuid}\Description", "Type", (uint)BcdTypeDeviceObject);
            WriteBcdStringElement(hObjects, sdiGuid, ElemDescription, SwbiBrand.BcdSdiDeviceName);
            WriteBcdBinaryElement(hObjects, sdiGuid, ElemRamdiskSdiDevice, sdiQualifiedPartBlob);
            WriteBcdStringElement(hObjects, sdiGuid, ElemRamdiskSdiPath, @"\boot\boot.sdi");

            // 2. Create Windows Boot Manager OS Loader Object (Type 0x10200003) via REG_OPTION_BACKUP_RESTORE
            SetBcdDwordValue(hObjects, $@"{loaderGuid}\Description", "Type", (uint)BcdTypeOsLoader);
            WriteBcdStringElement(hObjects, loaderGuid, ElemDescription, SwbiBrand.BootChooserEntryName);
            WriteBcdBinaryElement(hObjects, loaderGuid, ElemDevice, ramdiskDeviceBlob);
            WriteBcdBinaryElement(hObjects, loaderGuid, ElemOsDevice, ramdiskDeviceBlob);
            WriteBcdStringElement(hObjects, loaderGuid, ElemPath, winloadPath);
            WriteBcdStringElement(hObjects, loaderGuid, ElemSystemRoot, @"\Windows");
            WriteBcdBinaryElement(hObjects, loaderGuid, ElemNx, new byte[8]); // OptIn (0)
            WriteBcdBinaryElement(hObjects, loaderGuid, ElemDetectHal, new byte[] { 0x01 });
            WriteBcdBinaryElement(hObjects, loaderGuid, ElemWinPe, new byte[] { 0x01 });

            // 3. Register in {bootmgr} displayorder AND one-time bootsequence
            AppendMultiSzGuidElement(hObjects, BootMgrObjectGuid, ElemDisplayOrder, loaderGuid);
            SetMultiSzElement(hObjects, BootMgrObjectGuid, ElemBootSequence, new[] { loaderGuid });

            int flushError = Win32StorageInterop.RegFlushKey(hObjects);
            if (flushError != 0) throw new System.ComponentModel.Win32Exception(flushError, "Failed to flush the legacy BCD hive.");
            logger.Info("NativeBCD", $"Provisioned '{SwbiBrand.BootChooserEntryName}' ({loaderGuid}) directly in HKLM\\BCD00000000.");
            return (sdiGuid, loaderGuid);
        }
        catch (Exception error)
        {
            // Raw BCD writes can fail midway. Do not delete the partition or claim rollback succeeded.
            throw new FirmwareBootProvisionException(
                "Legacy BCD provisioning may be incomplete. Keep the installer partition and inspect the saved BCD backup before restarting.",
                cleanupIncomplete: true, inner: error);
        }
    }

    public static void FlushSystemBcdBeforeReboot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Win32StorageInterop.EnableProcessPrivilege("SeRestorePrivilege");
            Win32StorageInterop.EnableProcessPrivilege("SeBackupPrivilege");
            if (Win32StorageInterop.RegOpenKeyExW(
                    Win32StorageInterop.HKEY_LOCAL_MACHINE_PTR,
                    "BCD00000000",
                    Win32StorageInterop.REG_OPTION_BACKUP_RESTORE,
                    Win32StorageInterop.KEY_ALL_ACCESS_MASK,
                    out IntPtr hBcd) == 0 && hBcd != IntPtr.Zero)
            {
                try
                {
                    Win32StorageInterop.RegFlushKey(hBcd);
                }
                finally
                {
                    Win32StorageInterop.RegCloseKey(hBcd);
                }
            }
        }
        catch
        {
            // Ignore best-effort flush errors
        }
    }

    /// <summary>
    /// Registers the installer's own EFI bootloader in firmware without touching host or installer BCD stores.
    /// Firmware support, Secure Boot policy, and the ISO's standalone BCD determine whether it can boot.
    /// </summary>
    public static string ProvisionUefiNvramBootEntry(string installerDriveLetter, ISafeLogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("UEFI boot registration requires Windows.");
        }
        if (!Win32StorageInterop.EnableProcessPrivilege("SeSystemEnvironmentPrivilege"))
        {
            throw new InvalidOperationException("Unable to enable UEFI firmware-variable privileges.");
        }

        string cleanLetter = installerDriveLetter.TrimEnd(':', '\\', '/').ToUpperInvariant();
        string root = $"{cleanLetter}:\\";
        string bootFile = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "bootaa64.efi",
            Architecture.X86 => "bootia32.efi",
            Architecture.X64 => "bootx64.efi",
            _ => throw new InvalidOperationException("The current firmware architecture is unsupported.")
        };
        string localEfiPath = Path.Combine(root, "efi", "boot", bootFile);
        string localBcdPath = Path.Combine(root, "efi", "microsoft", "boot", "bcd");
        if (!File.Exists(localEfiPath) || new FileInfo(localEfiPath).Length == 0 ||
            !File.Exists(localBcdPath) || new FileInfo(localBcdPath).Length == 0)
        {
            throw new InvalidOperationException(
                $"The installer must contain its own EFI bootloader ({bootFile}) and efi\\microsoft\\boot\\bcd. " +
                "Use complete installation media; SWBI will not modify the host or synthesize a BCD store.");
        }

        (RawDiskLayoutSnapshot diskLayout, RawPartitionLayoutEntry swbiPart) = ResolvePartitionOnDisk(cleanLetter);
        int diskNumber = GetDiskNumberForDriveLetter(cleanLetter);
        using SafeFileHandle diskHandle = Win32StorageInterop.CreateFileW(
            $@"\\.\PhysicalDrive{diskNumber}",
            Win32StorageInterop.FILE_READ_ATTRIBUTES | Win32StorageInterop.SYNCHRONIZE,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE,
            IntPtr.Zero, Win32StorageInterop.OPEN_EXISTING, 0, IntPtr.Zero);
        if (diskHandle.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the installer disk to register its UEFI path.");
        }

        if (!Win32StorageInterop.TryQueryLogicalSectorSize(diskHandle, out int sectorSize))
            throw new InvalidOperationException("Cannot verify the installer disk's logical sector size for the UEFI device path.");
        string efiRelPath = $@"\EFI\BOOT\{bootFile.ToUpperInvariant()}";
        byte[] loadOption = BuildUefiLoadOptionBytes(SwbiBrand.BootChooserEntryName,
            diskLayout, swbiPart, sectorSize, efiRelPath);
        // Do not perform fallible work after registration before returning its identifier.
        return UefiBootRegistration.Register(new WindowsFirmwareVariableStore(), loadOption);
    }

    public static void RollbackRegistryAndUefiEntries(
        string? osLoaderGuid,
        string? sdiDeviceGuid,
        string? firmwareBootId,
        ISafeLogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Firmware-only cleanup must not open or flush the host's BCD hive.
        if (!string.IsNullOrWhiteSpace(firmwareBootId) &&
            firmwareBootId.StartsWith("UEFI:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Win32StorageInterop.EnableProcessPrivilege("SeSystemEnvironmentPrivilege"))
            {
                throw new InvalidOperationException("Unable to enable UEFI firmware-variable privileges for cleanup.");
            }
            UefiBootRegistration.Remove(new WindowsFirmwareVariableStore(), firmwareBootId);
        }

        if (string.IsNullOrWhiteSpace(osLoaderGuid) && string.IsNullOrWhiteSpace(sdiDeviceGuid) &&
            (string.IsNullOrWhiteSpace(firmwareBootId) || !firmwareBootId.StartsWith('{')))
        {
            return;
        }

        // Clean legacy or previous-version BCD entries when explicitly identified.
        try
        {
            Win32StorageInterop.EnableProcessPrivilege("SeRestorePrivilege");
            Win32StorageInterop.EnableProcessPrivilege("SeBackupPrivilege");
            using SystemBcdObjectsScope? scope = SystemBcdObjectsScope.TryOpen(writable: true);
            if (scope is null || scope.ObjectsHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("The loaded system BCD is unavailable for cleanup.");
            }

            IntPtr hObjects = scope.ObjectsHandle;

            if (!string.IsNullOrWhiteSpace(osLoaderGuid))
            {
                VerifyOwnedBcdObject(hObjects, osLoaderGuid, isSdi: false);
                RemoveGuidFromMultiSzElement(hObjects, BootMgrObjectGuid, ElemDisplayOrder, osLoaderGuid);
                RemoveGuidFromMultiSzElement(hObjects, BootMgrObjectGuid, ElemBootSequence, osLoaderGuid);
                DeleteKeyTreeBackupRestore(hObjects, osLoaderGuid);
            }

            if (!string.IsNullOrWhiteSpace(sdiDeviceGuid))
            {
                VerifyOwnedBcdObject(hObjects, sdiDeviceGuid, isSdi: true);
                DeleteKeyTreeBackupRestore(hObjects, sdiDeviceGuid);
            }

            if (!string.IsNullOrWhiteSpace(firmwareBootId) && firmwareBootId.StartsWith('{'))
            {
                VerifyOwnedBcdObject(hObjects, firmwareBootId, isSdi: false);
                RemoveGuidFromMultiSzElement(hObjects, FwBootMgrObjectGuid, ElemDisplayOrder, firmwareBootId);
                DeleteKeyTreeBackupRestore(hObjects, firmwareBootId);
            }

            int flushError = Win32StorageInterop.RegFlushKey(hObjects);
            if (flushError != 0) throw new System.ComponentModel.Win32Exception(flushError, "Failed to flush BCD cleanup.");
        }
        catch (Exception ex)
        {
            logger.Warn("NativeBCD", $"Registry BCD cleanup warning: {ex.Message}");
            throw new InvalidOperationException("BCD cleanup could not be verified; keep the installer partition.", ex);
        }
    }

    /// <summary>
    /// Initiates system reboot directly via Win32 InitiateSystemShutdownExW (SeShutdownPrivilege) — zero shutdown.exe!
    /// </summary>
    public static bool TryNativeSystemReboot(int delaySeconds, string message, out int win32Error)
    {
        Win32StorageInterop.EnableProcessPrivilege("SeShutdownPrivilege");
        const uint SHTDN_REASON_MAJOR_OPERATINGSYSTEM = 0x00020000;
        const uint SHTDN_REASON_MINOR_RECONFIG = 0x00000004;
        const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

        bool ok = Win32StorageInterop.InitiateSystemShutdownExW(
            null,
            message,
            (uint)Math.Clamp(delaySeconds, 0, 60),
            bForceAppsClosed: true,
            bRebootAfterShutdown: true,
            SHTDN_REASON_MAJOR_OPERATINGSYSTEM | SHTDN_REASON_MINOR_RECONFIG | SHTDN_REASON_FLAG_PLANNED);

        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    /// <summary>
    /// Builds an authentic 88-byte BCD QualifiedPartition element (16-byte AdditionalOptions GUID + 72-byte QualifiedPartition).
    /// Verified 1:1 byte-for-byte against Windows bcdedit.exe / bcd.dll.
    /// </summary>
    public static byte[] BuildBcdQualifiedPartitionElement(
        Guid additionalOptionsGuid,
        RawDiskLayoutSnapshot diskLayout,
        RawPartitionLayoutEntry part)
    {
        byte[] blob = new byte[16 + 72]; // 88 bytes
        additionalOptionsGuid.TryWriteBytes(blob.AsSpan(0, 16));
        uint flags = additionalOptionsGuid == Guid.Empty ? 0u : 1u;
        WriteQualifiedPartitionStruct72(blob.AsSpan(16, 72), diskLayout, part, flags);
        return blob;
    }

    /// <summary>
    /// Builds an authentic BCD RamdiskDevice element verified 1:1 byte-for-byte against Windows bcdedit.exe / bcd.dll:
    ///   [0..15]    AdditionalOptions GUID (SdiOptionsGuid)
    ///   [16..31]   Outer BlockIo BcdDeviceData header: DeviceType = 0 (BlockIo), Flags = (sdiOptionsGuid != Empty ? 1 : 0), Size = 52 + 72 + pathBytes.Length
    ///   [32..35]   BlockIoType = 3 (Ramdisk)
    ///   [36..55]   Ramdisk parameters (20 zero bytes: ImageBase=0, ImageLength=0, ImageOffset=0)
    ///   [56..67]   Inner FileDevice locator header (12 bytes): Type=1, Size = 12 + 72 + pathBytes.Length, Version=5
    ///   [68..139]  Inner ParentDevice (72-byte QualifiedPartition)
    ///   [140..]    Null-terminated UTF-16LE WIM path ("\sources\boot.wim\0")
    /// </summary>
    public static byte[] BuildBcdRamdiskDeviceElement(
        Guid sdiOptionsGuid,
        RawDiskLayoutSnapshot diskLayout,
        RawPartitionLayoutEntry part,
        string wimRelativePathWithLeadingSlash)
    {
        string normPath = wimRelativePathWithLeadingSlash.StartsWith('\\')
            ? wimRelativePathWithLeadingSlash
            : "\\" + wimRelativePathWithLeadingSlash;

        byte[] pathBytes = Encoding.Unicode.GetBytes(normPath + "\0");
        int fileLocatorSize = 12 + 72 + pathBytes.Length;
        int ramdiskStructSize = 40 + fileLocatorSize; // 52 + 72 + pathBytes.Length
        byte[] full = new byte[16 + ramdiskStructSize];

        sdiOptionsGuid.TryWriteBytes(full.AsSpan(0, 16));

        Span<byte> rd = full.AsSpan(16, ramdiskStructSize);
        BinaryPrimitives.WriteUInt32LittleEndian(rd.Slice(0, 4), 0); // DeviceType = 0 (BCDE_DEVICE_TYPE_BLOCK_IO)
        BinaryPrimitives.WriteUInt32LittleEndian(rd.Slice(4, 4), sdiOptionsGuid == Guid.Empty ? 0u : 1u); // Flags (1 = HasAdditionalOptions)
        BinaryPrimitives.WriteUInt64LittleEndian(rd.Slice(8, 8), (ulong)ramdiskStructSize);
        BinaryPrimitives.WriteUInt32LittleEndian(rd.Slice(16, 4), 3); // BlockIoType = 3 (Ramdisk)
        // rd[20..39] = 20 zero bytes (Ramdisk ImageBase=0, ImageLength=0, ImageOffset=0)

        // Nested FileDevice locator at rd[40..51]:
        BinaryPrimitives.WriteUInt32LittleEndian(rd.Slice(40, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(rd.Slice(44, 4), (uint)fileLocatorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(rd.Slice(48, 4), 5);

        WriteQualifiedPartitionStruct72(rd.Slice(52, 72), diskLayout, part, flags: 0);
        pathBytes.CopyTo(rd.Slice(124));

        return full;
    }

    /// <summary>
    /// Builds a UEFI Spec Chapter 3 EFI_LOAD_OPTION binary payload (HARDDRIVE_DP + FILEPATH_DP + END_DP).
    /// </summary>
    public static byte[] BuildUefiLoadOptionBytes(
        string description,
        RawDiskLayoutSnapshot diskLayout,
        RawPartitionLayoutEntry part,
        int logicalSectorBytes,
        string efiBootFilePath)
    {
        int bps = logicalSectorBytes > 0 ? logicalSectorBytes : 512;
        byte[] descBytes = Encoding.Unicode.GetBytes(description + "\0");
        byte[] filePathBytes = Encoding.Unicode.GetBytes(efiBootFilePath + "\0");

        // 1. HARDDRIVE_DEVICE_PATH (Type 4, SubType 1, Length 42)
        byte[] hdNode = new byte[42];
        hdNode[0] = 0x04;
        hdNode[1] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(hdNode.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(hdNode.AsSpan(4, 4), (uint)Math.Max(1, part.PartitionNumber));
        BinaryPrimitives.WriteUInt64LittleEndian(hdNode.AsSpan(8, 8), (ulong)(part.StartingOffsetBytes / bps));
        BinaryPrimitives.WriteUInt64LittleEndian(hdNode.AsSpan(16, 8), (ulong)Math.Max(1L, part.PartitionLengthBytes / bps));

        if (diskLayout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT)
        {
            part.GptPartitionId.TryWriteBytes(hdNode.AsSpan(24, 16));
            hdNode[40] = 0x02; // MBRType = GPT
            hdNode[41] = 0x02; // SignatureType = GUID
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(hdNode.AsSpan(24, 4), diskLayout.MbrSignature);
            hdNode[40] = 0x01; // MBRType = PC-AT MBR
            hdNode[41] = 0x01; // SignatureType = 32-bit MBR signature
        }

        // 2. FILEPATH_DEVICE_PATH (Type 4, SubType 4, Length = 4 + filePathBytes.Length)
        byte[] fileNode = new byte[4 + filePathBytes.Length];
        fileNode[0] = 0x04;
        fileNode[1] = 0x04;
        BinaryPrimitives.WriteUInt16LittleEndian(fileNode.AsSpan(2, 2), (ushort)fileNode.Length);
        Buffer.BlockCopy(filePathBytes, 0, fileNode, 4, filePathBytes.Length);

        // 3. END_DEVICE_PATH (Type 0x7F, SubType 0xFF, Length 4)
        byte[] endNode = { 0x7F, 0xFF, 0x04, 0x00 };

        ushort dpListLen = (ushort)(hdNode.Length + fileNode.Length + endNode.Length);
        byte[] loadOption = new byte[4 + 2 + descBytes.Length + dpListLen];

        BinaryPrimitives.WriteUInt32LittleEndian(loadOption.AsSpan(0, 4), 0x00000001u); // LOAD_OPTION_ACTIVE
        BinaryPrimitives.WriteUInt16LittleEndian(loadOption.AsSpan(4, 2), dpListLen);
        Buffer.BlockCopy(descBytes, 0, loadOption, 6, descBytes.Length);

        int dpOff = 6 + descBytes.Length;
        Buffer.BlockCopy(hdNode, 0, loadOption, dpOff, hdNode.Length);
        dpOff += hdNode.Length;
        Buffer.BlockCopy(fileNode, 0, loadOption, dpOff, fileNode.Length);
        dpOff += fileNode.Length;
        Buffer.BlockCopy(endNode, 0, loadOption, dpOff, endNode.Length);

        return loadOption;
    }

    /// <summary>
    /// Writes a 72-byte (0x48) BCD QualifiedPartition (PartitionEx, DeviceType = 6) structure:
    ///   [0..3]   DeviceType = 6 (QualifiedPartition)
    ///   [4..7]   Flags
    ///   [8..15]  Size = 0x48 (72 bytes)
    ///   [16..31] PartitionIdentifier union: GPT PartitionGuid (16B) OR MBR PartitionOffsetBytes (8B + 8B zero)
    ///   [32..35] PartitionStyle / DiskKind: 0 = GPT, 1 = MBR
    ///   [36..39] Reserved = 0
    ///   [40..55] DiskIdentifier union: GPT DiskGuid (16B) OR MBR DiskSignature (4B + 12B zero)
    ///   [56..71] Padding (16 zero bytes)
    /// </summary>
    private static void WriteQualifiedPartitionStruct72(
        Span<byte> dest72,
        RawDiskLayoutSnapshot diskLayout,
        RawPartitionLayoutEntry part,
        uint flags = 0)
    {
        dest72.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(dest72.Slice(0, 4), 6);     // DeviceType = QualifiedPartition
        BinaryPrimitives.WriteUInt32LittleEndian(dest72.Slice(4, 4), flags); // Flags
        BinaryPrimitives.WriteUInt64LittleEndian(dest72.Slice(8, 8), 0x48);  // Size = 72 bytes

        if (diskLayout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT)
        {
            part.GptPartitionId.TryWriteBytes(dest72.Slice(16, 16));         // [16..31] PartitionGuid FIRST
            BinaryPrimitives.WriteUInt32LittleEndian(dest72.Slice(32, 4), 0); // [32..35] 0 = GPT
            diskLayout.GptDiskId.TryWriteBytes(dest72.Slice(40, 16));        // [40..55] DiskGuid SECOND
        }
        else
        {
            BinaryPrimitives.WriteInt64LittleEndian(dest72.Slice(16, 8), part.StartingOffsetBytes); // [16..23] MBR PartitionOffset
            BinaryPrimitives.WriteUInt32LittleEndian(dest72.Slice(32, 4), 1);                       // [32..35] 1 = MBR
            BinaryPrimitives.WriteUInt32LittleEndian(dest72.Slice(40, 4), diskLayout.MbrSignature); // [40..43] MBR DiskSignature
        }
    }

    private static (RawDiskLayoutSnapshot Layout, RawPartitionLayoutEntry Part) ResolvePartitionOnDisk(string cleanLetter)
    {
        using SafeFileHandle vh = Win32StorageInterop.CreateFileW(
            $@"\\.\{cleanLetter}:",
            0,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (vh.IsInvalid)
        {
            throw new InvalidOperationException($"Unable to open volume '\\\\.\\{cleanLetter}:'.");
        }

        if (!Win32StorageInterop.TryGetSingleVolumeDiskExtent(vh, out int diskNum, out long startOffset))
        {
            throw new InvalidOperationException($"Cannot identify a single disk extent for {cleanLetter}:.");
        }

        using SafeFileHandle dh = Win32StorageInterop.CreateFileW(
            $@"\\.\PhysicalDrive{diskNum}",
            0,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        RawDiskLayoutSnapshot layout = Win32StorageInterop.TryReadDiskLayout(dh)
            ?? throw new InvalidOperationException($"Unable to read partition layout on PhysicalDrive{diskNum}.");

        RawPartitionLayoutEntry part = layout.Partitions.FirstOrDefault(p =>
                p.StartingOffsetBytes == startOffset)
            ?? throw new InvalidOperationException($"Unable to locate partition {cleanLetter}: on PhysicalDrive{diskNum}.");

        return (layout, part);
    }

    private static int GetDiskNumberForDriveLetter(string cleanLetter)
    {
        using SafeFileHandle vh = Win32StorageInterop.CreateFileW(
            $@"\\.\{cleanLetter}:",
            Win32StorageInterop.FILE_READ_ATTRIBUTES | Win32StorageInterop.SYNCHRONIZE,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (!vh.IsInvalid && Win32StorageInterop.TryGetSingleVolumeDiskExtent(vh, out int d, out _))
        {
            return d;
        }

        throw new InvalidOperationException($"Unable to determine the disk for {cleanLetter}:; refusing to register a firmware path.");
    }

    private static bool TryFindExistingSwbiUefiBootSlot(out ushort slot)
    {
        slot = 0;
        Win32StorageInterop.EnableProcessPrivilege("SeSystemEnvironmentPrivilege");
        foreach (ushort cand in ReadUefiUint16List("BootOrder"))
        {
            byte[]? val = ReadUefiVariable($"Boot{cand:X4}");
            if (val is not null && UefiBootRegistration.IsSwbiLoadOption(val))
            {
                slot = cand;
                return true;
            }
        }
        return false;
    }

    private static byte[]? ReadUefiVariable(string name)
    {
        byte[] buf = new byte[4096];
        uint len = Win32StorageInterop.GetFirmwareEnvironmentVariableExW(
            name,
            Win32StorageInterop.EfiGlobalVariableNamespaceGuid,
            buf,
            (uint)buf.Length,
            out _);

        if (len == 0 || len > buf.Length)
        {
            return null;
        }

        byte[] copy = new byte[len];
        Buffer.BlockCopy(buf, 0, copy, 0, (int)len);
        return copy;
    }

    private static List<ushort> ReadUefiUint16List(string name)
    {
        var list = new List<ushort>();
        byte[]? raw = ReadUefiVariable(name);
        if (raw is null) return list;

        for (int i = 0; i + 2 <= raw.Length; i += 2)
        {
            list.Add(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i, 2)));
        }

        return list;
    }

    private static IntPtr OpenOrCreateBackupRestoreSubKey(IntPtr hParent, string subKey)
    {
        int rc = Win32StorageInterop.RegCreateKeyExW(
            hParent,
            subKey,
            0,
            null,
            Win32StorageInterop.REG_OPTION_BACKUP_RESTORE,
            Win32StorageInterop.KEY_ALL_ACCESS_MASK,
            IntPtr.Zero,
            out IntPtr hChild,
            out _);

        if (rc != 0 || hChild == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(rc, $"RegCreateKeyExW('{subKey}') with REG_OPTION_BACKUP_RESTORE failed (Win32 {rc}).");
        }

        return hChild;
    }

    private static void SetBcdDwordValue(IntPtr hObjects, string subKey, string valueName, uint value)
    {
        IntPtr hKey = OpenOrCreateBackupRestoreSubKey(hObjects, subKey);
        try
        {
            byte[] buf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
            int rc = Win32StorageInterop.RegSetValueExW(hKey, valueName, 0, Win32StorageInterop.REG_DWORD, buf, 4);
            if (rc != 0)
            {
                throw new System.ComponentModel.Win32Exception(rc, $"RegSetValueExW('{subKey}\\{valueName}') failed (Win32 {rc}).");
            }
        }
        finally
        {
            Win32StorageInterop.RegCloseKey(hKey);
        }
    }

    private static void WriteBcdStringElement(IntPtr hObjects, string objectGuid, string hexElemId, string value)
    {
        IntPtr hKey = OpenOrCreateBackupRestoreSubKey(hObjects, $@"{objectGuid}\Elements\{hexElemId}");
        try
        {
            byte[] bytes = Encoding.Unicode.GetBytes(value + "\0");
            int rc = Win32StorageInterop.RegSetValueExW(hKey, "Element", 0, Win32StorageInterop.REG_SZ, bytes, (uint)bytes.Length);
            if (rc != 0)
            {
                throw new System.ComponentModel.Win32Exception(rc, $"RegSetValueExW('{objectGuid}\\Elements\\{hexElemId}') failed (Win32 {rc}).");
            }
        }
        finally
        {
            Win32StorageInterop.RegCloseKey(hKey);
        }
    }

    private static void WriteBcdBinaryElement(IntPtr hObjects, string objectGuid, string hexElemId, byte[] value)
    {
        IntPtr hKey = OpenOrCreateBackupRestoreSubKey(hObjects, $@"{objectGuid}\Elements\{hexElemId}");
        try
        {
            int rc = Win32StorageInterop.RegSetValueExW(hKey, "Element", 0, Win32StorageInterop.REG_BINARY, value, (uint)value.Length);
            if (rc != 0)
            {
                throw new System.ComponentModel.Win32Exception(rc, $"RegSetValueExW('{objectGuid}\\Elements\\{hexElemId}') failed (Win32 {rc}).");
            }
        }
        finally
        {
            Win32StorageInterop.RegCloseKey(hKey);
        }
    }

    private static string[] ReadMultiSzValue(IntPtr hKey, string valueName)
    {
        uint size = 0;
        int rc = Win32StorageInterop.RegQueryValueExW(hKey, valueName, IntPtr.Zero, out uint type, null, ref size);
        if (rc == 2) return Array.Empty<string>();
        if (rc != 0) throw new System.ComponentModel.Win32Exception(rc, "Unable to read a BCD list element.");
        if (size < 2 || type != Win32StorageInterop.REG_MULTI_SZ)
            throw new InvalidOperationException("The BCD list element has an unexpected format.");

        byte[] raw = new byte[size];
        rc = Win32StorageInterop.RegQueryValueExW(hKey, valueName, IntPtr.Zero, out _, raw, ref size);
        if (rc != 0) throw new System.ComponentModel.Win32Exception(rc, "Unable to read a BCD list element.");
        return Encoding.Unicode.GetString(raw, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static byte[] EncodeMultiSz(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return new byte[2]; // single empty terminator
        }

        var sb = new StringBuilder();
        foreach (string v in values)
        {
            sb.Append(v).Append('\0');
        }
        sb.Append('\0');
        return Encoding.Unicode.GetBytes(sb.ToString());
    }

    private static void SetMultiSzElement(IntPtr hObjects, string objectGuid, string hexElemId, string[] values)
    {
        string subKey = $@"{objectGuid}\Elements\{hexElemId}";
        if (Win32StorageInterop.RegOpenKeyExW(hObjects, objectGuid, Win32StorageInterop.REG_OPTION_BACKUP_RESTORE, KEY_READ, out IntPtr hCheck) != 0)
        {
            throw new InvalidOperationException($"BCD object {objectGuid} is unavailable.");
        }
        Win32StorageInterop.RegCloseKey(hCheck);

        IntPtr hKey = OpenOrCreateBackupRestoreSubKey(hObjects, subKey);
        try
        {
            byte[] encoded = EncodeMultiSz(values);
            int writeError = Win32StorageInterop.RegSetValueExW(hKey, "Element", 0, Win32StorageInterop.REG_MULTI_SZ, encoded, (uint)encoded.Length);
            if (writeError != 0) throw new System.ComponentModel.Win32Exception(writeError, "Failed to update a BCD list element.");
        }
        finally
        {
            Win32StorageInterop.RegCloseKey(hKey);
        }
    }

    private static void AppendMultiSzGuidElement(IntPtr hObjects, string objectGuid, string hexElemId, string guid)
    {
        if (Win32StorageInterop.RegOpenKeyExW(hObjects, objectGuid, Win32StorageInterop.REG_OPTION_BACKUP_RESTORE, KEY_READ, out IntPtr hCheck) != 0)
        {
            throw new InvalidOperationException($"BCD object {objectGuid} is unavailable.");
        }
        Win32StorageInterop.RegCloseKey(hCheck);

        IntPtr hKey = OpenOrCreateBackupRestoreSubKey(hObjects, $@"{objectGuid}\Elements\{hexElemId}");
        try
        {
            string[] existing = ReadMultiSzValue(hKey, "Element");
            if (!existing.Contains(guid, StringComparer.OrdinalIgnoreCase))
            {
                byte[] encoded = EncodeMultiSz(existing.Append(guid).ToArray());
                int writeError = Win32StorageInterop.RegSetValueExW(hKey, "Element", 0, Win32StorageInterop.REG_MULTI_SZ, encoded, (uint)encoded.Length);
                if (writeError != 0) throw new System.ComponentModel.Win32Exception(writeError, "Failed to update a BCD list element.");
            }
        }
        finally
        {
            Win32StorageInterop.RegCloseKey(hKey);
        }
    }

    private static void VerifyOwnedBcdObject(IntPtr objects, string id, bool isSdi)
    {
        if (!Guid.TryParse(id, out Guid parsed) || parsed == Guid.Empty)
            throw new InvalidOperationException("Invalid BCD identifier in the installer manifest.");

        int objectError = Win32StorageInterop.RegOpenKeyExW(objects, id,
            Win32StorageInterop.REG_OPTION_BACKUP_RESTORE, KEY_READ, out IntPtr objectKey);
        if (objectError == 2) return; // Already absent; removal of references to this GUID is safe.
        if (objectError != 0 || objectKey == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(objectError, "Cannot verify BCD entry ownership.");
        Win32StorageInterop.RegCloseKey(objectKey);

        int error = Win32StorageInterop.RegOpenKeyExW(objects, $@"{id}\Elements\{ElemDescription}",
            Win32StorageInterop.REG_OPTION_BACKUP_RESTORE, KEY_READ, out IntPtr descriptionKey);
        if (error != 0 || descriptionKey == IntPtr.Zero)
            throw new InvalidOperationException($"BCD entry {id} has no verifiable SWBI description.");
        try
        {
            uint size = 0;
            error = Win32StorageInterop.RegQueryValueExW(descriptionKey, "Element", IntPtr.Zero, out uint type, null, ref size);
            if (error != 0 || type != Win32StorageInterop.REG_SZ || size < 2 || size > 4096)
                throw new InvalidOperationException($"BCD entry {id} has an invalid description.");
            byte[] bytes = new byte[size];
            error = Win32StorageInterop.RegQueryValueExW(descriptionKey, "Element", IntPtr.Zero, out _, bytes, ref size);
            if (error != 0) throw new System.ComponentModel.Win32Exception(error, "Cannot read the BCD description.");
            string title = Encoding.Unicode.GetString(bytes, 0, (int)size).TrimEnd('\0');
            if (isSdi ? !SwbiBrand.IsRecognizedSdiDescription(title) : !SwbiBrand.IsRecognizedBootDescription(title))
                throw new InvalidOperationException($"BCD entry {id} no longer belongs to SWBI; it was not removed.");
        }
        finally { Win32StorageInterop.RegCloseKey(descriptionKey); }
    }

    private static void RemoveGuidFromMultiSzElement(IntPtr hObjects, string objectGuid, string hexElemId, string guid)
    {
        string elemPath = $@"{objectGuid}\Elements\{hexElemId}";
        int openError = Win32StorageInterop.RegOpenKeyExW(
            hObjects, elemPath, Win32StorageInterop.REG_OPTION_BACKUP_RESTORE,
            Win32StorageInterop.KEY_ALL_ACCESS_MASK, out IntPtr hKey);
        if (openError == 2) return;
        if (openError != 0 || hKey == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(openError, "Cannot open BCD list for cleanup.");

        string[] existing;
        string[] filtered;
        try
        {
            existing = ReadMultiSzValue(hKey, "Element");
            filtered = existing.Where(x => !string.Equals(x, guid, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (filtered.Length > 0 && filtered.Length != existing.Length)
            {
                byte[] encoded = EncodeMultiSz(filtered);
                int writeError = Win32StorageInterop.RegSetValueExW(hKey, "Element", 0, Win32StorageInterop.REG_MULTI_SZ, encoded, (uint)encoded.Length);
                if (writeError != 0) throw new System.ComponentModel.Win32Exception(writeError, "Failed to update a BCD list element.");
            }
        }
        finally
        {
            Win32StorageInterop.RegCloseKey(hKey);
        }

        if (filtered.Length == 0 && existing.Length > 0)
        {
            DeleteKeyTreeBackupRestore(hObjects, elemPath);
        }
    }

    private static void DeleteKeyTreeBackupRestore(IntPtr hParent, string subKey)
    {
        if (string.IsNullOrWhiteSpace(subKey))
        {
            return;
        }

        int rc = Win32StorageInterop.RegOpenKeyExW(
            hParent,
            subKey,
            Win32StorageInterop.REG_OPTION_BACKUP_RESTORE,
            Win32StorageInterop.KEY_ALL_ACCESS_MASK,
            out IntPtr hKey);

        if (rc == 2) return;
        if (rc != 0 || hKey == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(rc, $"Cannot open BCD key '{subKey}' for deletion.");
        }

        try
        {
            string[] childNames;
            using (var safeHandle = new Microsoft.Win32.SafeHandles.SafeRegistryHandle(hKey, ownsHandle: false))
            using (var regKey = RegistryKey.FromHandle(safeHandle))
            {
                childNames = regKey.GetSubKeyNames();
            }

            foreach (string child in childNames)
            {
                DeleteKeyTreeBackupRestore(hKey, child);
            }

            int status = Win32StorageInterop.NtDeleteKey(hKey);
            if (status != 0) throw new InvalidOperationException($"Could not delete BCD key '{subKey}' (NTSTATUS 0x{status:X8}).");
        }
        finally
        {
            Win32StorageInterop.RegCloseKey(hKey);
        }
    }

    /// <summary>Opens only the loaded system BCD; never mount an arbitrarily discovered store.</summary>
    private sealed class SystemBcdObjectsScope : IDisposable
    {
        public RegistryKey? ObjectsKey { get; }
        public IntPtr ObjectsHandle { get; private set; }

        private SystemBcdObjectsScope(RegistryKey? objectsKey, IntPtr objectsHandle)
        {
            ObjectsKey = objectsKey;
            ObjectsHandle = objectsHandle;
        }

        public static SystemBcdObjectsScope? TryOpen(bool writable)
        {
            Win32StorageInterop.EnableProcessPrivilege("SeBackupPrivilege");
            if (writable) Win32StorageInterop.EnableProcessPrivilege("SeRestorePrivilege");
            uint access = writable ? Win32StorageInterop.KEY_ALL_ACCESS_MASK : KEY_READ;
            uint options = writable ? Win32StorageInterop.REG_OPTION_BACKUP_RESTORE : 0u;
            int error = Win32StorageInterop.RegOpenKeyExW(Win32StorageInterop.HKEY_LOCAL_MACHINE_PTR,
                @"BCD00000000\Objects", options, access, out IntPtr handle);
            if (error != 0 || handle == IntPtr.Zero) return null;
            RegistryKey? view = !writable ? Registry.LocalMachine.OpenSubKey(@"BCD00000000\Objects", false) : null;
            return new SystemBcdObjectsScope(view, handle);
        }

        public void Dispose()
        {
            ObjectsKey?.Dispose();
            if (ObjectsHandle != IntPtr.Zero)
            {
                Win32StorageInterop.RegCloseKey(ObjectsHandle);
                ObjectsHandle = IntPtr.Zero;
            }
        }
    }

}
