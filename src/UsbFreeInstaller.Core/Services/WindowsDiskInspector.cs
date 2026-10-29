using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services.Native;

namespace UsbFreeInstaller.Core.Services;

/// <summary>
/// 100% Native Win32 Kernel + Registry Disk, Partition, NTFS, Firmware, BitLocker & Hardware Inspector.
/// Operates in-process via DeviceIoControl (IOCTL_DISK_GET_DRIVE_LAYOUT_EX, IOCTL_STORAGE_QUERY_PROPERTY,
/// FSCTL_GET_NTFS_VOLUME_DATA), Volume Mount Manager APIs, TBS TPM APIs, and Registry — zero reliance on
/// PowerShell, WMI/CIM, or diskpart.exe.
/// </summary>
public sealed class WindowsDiskInspector : IDiskInspector
{
    private readonly IProcessRunner _processRunner;
    private readonly ISafeLogger _logger;

    public WindowsDiskInspector(IProcessRunner processRunner, ISafeLogger logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public Task<SystemEnvironmentSnapshot> InspectEnvironmentAsync(
        CancellationToken cancellationToken = default)
    {
        string hostArch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            var other => other.ToString().ToLowerInvariant()
        };

        if (!OperatingSystem.IsWindows())
        {
            _logger.Warn("Inspector", "Host OS is not Windows; returning non-Windows diagnostic snapshot.");
            return Task.FromResult(new SystemEnvironmentSnapshot
            {
                IsElevated = false,
                Firmware = FirmwareMode.Unknown,
                SecureBootEnabled = null,
                HostArchitecture = hostArch,
                SystemDriveLetter = "C",
                TargetDiskNumber = 0,
                TargetOsPartitionNumber = 0,
                AllDisks = Array.Empty<DiskSnapshot>()
            });
        }

        return Task.Run(() => InspectNativeWindowsEnvironment(hostArch, cancellationToken), cancellationToken);
    }

    private SystemEnvironmentSnapshot InspectNativeWindowsEnvironment(
        string hostArch,
        CancellationToken cancellationToken)
    {
        bool isElevated = Environment.IsPrivilegedProcess;
        FirmwareMode nativeFirmware = QueryNativeFirmwareMode();
        PowerStatusInfo powerStatus = QueryNativePowerStatus();
        bool? secureBoot = QueryNativeSecureBootState(nativeFirmware);

        string sysDriveRaw = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        string sysDriveLetter = sysDriveRaw.TrimEnd(':', '\\', '/').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(sysDriveLetter))
        {
            sysDriveLetter = "C";
        }

        CurrentWindowsInfo curWin = QueryCurrentWindowsFromRegistry(sysDriveLetter);
        PcHardwareCapabilities hwCaps = QueryHardwareCapabilitiesNative();
        BitLockerStatusInfo bitLocker = QueryBitLockerStatusNative(sysDriveLetter);

        // 1. Discover all mounted volumes & map them to (DiskNumber, StartingOffsetBytes / PartitionNumber)
        IReadOnlyList<RawVolumeBinding> mountedVolumes = Win32StorageInterop.EnumerateAllVolumes();

        // 2. Resolve System Drive (C:) DiskNumber + PartitionNumber + StartingOffset
        ResolveSystemDriveLocationNative(
            sysDriveLetter,
            mountedVolumes,
            out int targetDiskNumber,
            out int targetPartNumber,
            out long targetStartOffset);

        // 3. Enumerate all physical disks via kernel handles (\\.\PhysicalDrive0..N)
        SortedSet<int> candidateDiskIndices = DiscoverPhysicalDriveNumbers(targetDiskNumber, mountedVolumes);
        Dictionary<int, (string ServiceName, string ControllerName)> controllerMap = QueryStorageControllersFromRegistry(candidateDiskIndices);

        var disks = new List<DiskSnapshot>();
        foreach (int diskNum in candidateDiskIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiskSnapshot? snapshot = InspectSinglePhysicalDiskNative(
                diskNum,
                targetDiskNumber,
                ref targetPartNumber,
                targetStartOffset,
                sysDriveLetter,
                nativeFirmware,
                mountedVolumes,
                controllerMap);

            if (snapshot is not null)
            {
                disks.Add(snapshot);
            }
        }

        // Synthesize fallback system disk entry if non-elevated sandbox restricts raw PhysicalDrive open
        if (disks.Count == 0)
        {
            DiskSnapshot? synthetic = BuildFallbackSystemDiskFromDriveInfo(
                sysDriveLetter,
                targetDiskNumber,
                targetPartNumber,
                nativeFirmware);
            if (synthetic is not null)
            {
                disks.Add(synthetic);
            }
        }

        PartitionSnapshot? osPartition = disks
            .FirstOrDefault(d => d.DiskNumber == targetDiskNumber)?
            .Partitions.FirstOrDefault(p =>
                p.PartitionNumber == targetPartNumber ||
                string.Equals(p.DriveLetter, sysDriveLetter, StringComparison.OrdinalIgnoreCase));

        if (osPartition is not null)
        {
            targetPartNumber = osPartition.PartitionNumber;
        }

        ShrinkAnalysis? shrinkInfo = null;
        if (osPartition is not null)
        {
            long shrinkMinBytes = NativeNtfsShrinkEngine.QueryMinimumShrinkSizeBytes(
                sysDriveLetter,
                osPartition.SizeBytes,
                osPartition.FreeBytes);

            shrinkInfo = new ShrinkAnalysis
            {
                CurrentSizeBytes = osPartition.SizeBytes,
                MinimumSupportedSizeBytes = shrinkMinBytes,
                VolumeFreeBytes = osPartition.FreeBytes,
                OsSafetyReserveBytes = PartitionSizingCalculator.DefaultOsSafetyReserveBytes
            };
        }

        DiskSnapshot? targetDiskObj = disks.FirstOrDefault(d => d.DiskNumber == targetDiskNumber);
        _logger.Info(
            "Inspector",
            $"Native Win32 inspected PC: OS='{curWin.SummaryDisplay}', Drive='{targetDiskObj?.DriveKindDisplay} ({targetDiskObj?.FriendlyName})', Controller='{targetDiskObj?.ControllerServiceName}', Firmware={nativeFirmware}.");

        return new SystemEnvironmentSnapshot
        {
            IsElevated = isElevated,
            Firmware = nativeFirmware,
            SecureBootEnabled = secureBoot,
            HostArchitecture = hostArch,
            SystemDriveLetter = sysDriveLetter,
            TargetDiskNumber = targetDiskNumber,
            TargetOsPartitionNumber = targetPartNumber,
            AllDisks = disks,
            ShrinkInfo = shrinkInfo,
            BitLocker = bitLocker,
            PowerStatus = powerStatus,
            CurrentWindows = curWin,
            Hardware = hwCaps
        };
    }

    private static void ResolveSystemDriveLocationNative(
        string sysDriveLetter,
        IReadOnlyList<RawVolumeBinding> mountedVolumes,
        out int diskNumber,
        out int partitionNumber,
        out long startingOffsetBytes)
    {
        diskNumber = 0;
        partitionNumber = 1;
        startingOffsetBytes = 0;

        using SafeFileHandle sysHandle = Win32StorageInterop.CreateFileW(
            $@"\\.\{sysDriveLetter}:",
            0,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (!sysHandle.IsInvalid)
        {
            if (Win32StorageInterop.TryGetDeviceNumbers(sysHandle, out int dNum, out int pNum))
            {
                if (dNum >= 0) diskNumber = dNum;
                if (pNum > 0) partitionNumber = pNum;
            }
            if (Win32StorageInterop.TryGetVolumeFirstDiskExtent(sysHandle, out int extDisk, out long extOff, out _))
            {
                if (extDisk >= 0) diskNumber = extDisk;
                if (extOff > 0) startingOffsetBytes = extOff;
            }
            return;
        }

        RawVolumeBinding? match = mountedVolumes.FirstOrDefault(v =>
            string.Equals(v.DriveLetter, sysDriveLetter, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            if (match.DiskNumber >= 0) diskNumber = match.DiskNumber;
            if (match.PartitionNumber > 0) partitionNumber = match.PartitionNumber;
            if (match.StartingOffsetBytes > 0) startingOffsetBytes = match.StartingOffsetBytes;
        }
    }

    private static SortedSet<int> DiscoverPhysicalDriveNumbers(
        int targetDiskNumber,
        IReadOnlyList<RawVolumeBinding> mountedVolumes)
    {
        var indices = new SortedSet<int> { Math.Max(0, targetDiskNumber) };
        foreach (RawVolumeBinding mv in mountedVolumes)
        {
            if (mv.DiskNumber >= 0)
            {
                indices.Add(mv.DiskNumber);
            }
        }

        try
        {
            using RegistryKey? diskEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\disk\Enum");
            if (diskEnum?.GetValue("Count") is int count && count > 0)
            {
                for (int i = 0; i < Math.Min(count, 32); i++)
                {
                    if (diskEnum.GetValue(i.ToString()) is string)
                    {
                        indices.Add(i);
                    }
                }
            }
        }
        catch
        {
            // Ignore registry enum error
        }

        for (int probe = 0; probe < 16; probe++)
        {
            using SafeFileHandle h = Win32StorageInterop.CreateFileW(
                $@"\\.\PhysicalDrive{probe}",
                0,
                Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Win32StorageInterop.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (!h.IsInvalid)
            {
                indices.Add(probe);
            }
        }

        return indices;
    }

    private static DiskSnapshot? InspectSinglePhysicalDiskNative(
        int diskNum,
        int targetDiskNumber,
        ref int targetPartNumber,
        long targetStartOffset,
        string sysDriveLetter,
        FirmwareMode nativeFirmware,
        IReadOnlyList<RawVolumeBinding> mountedVolumes,
        Dictionary<int, (string ServiceName, string ControllerName)> controllerMap)
    {
        using SafeFileHandle diskHandle = Win32StorageInterop.CreateFileW(
            $@"\\.\PhysicalDrive{diskNum}",
            0,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (diskHandle.IsInvalid)
        {
            return null;
        }

        RawStorageHardwareProps hwProps = Win32StorageInterop.QueryStorageHardwareProperties(diskHandle, diskNum);
        string friendlyName = hwProps.FriendlyName;
        string busTypeStr = hwProps.BusTypeName;
        string mediaTypeStr = hwProps.IncursSeekPenalty == false ||
                              busTypeStr.Equals("NVMe", StringComparison.OrdinalIgnoreCase) ||
                              friendlyName.Contains("SSD", StringComparison.OrdinalIgnoreCase)
            ? "SSD"
            : hwProps.IncursSeekPenalty == true
                ? "HDD"
                : "Unspecified";

        long diskLengthBytes = Win32StorageInterop.TryQueryDeviceLengthBytesOrZero(diskHandle);
        RawDiskLayoutSnapshot? rawLayout = Win32StorageInterop.TryReadDiskLayout(diskHandle);

        PartitionStyleType partStyle = rawLayout switch
        {
            { PartitionStyle: Win32StorageInterop.PARTITION_STYLE_GPT } => PartitionStyleType.Gpt,
            { PartitionStyle: Win32StorageInterop.PARTITION_STYLE_MBR } => PartitionStyleType.Mbr,
            _ => (diskNum == targetDiskNumber && nativeFirmware == FirmwareMode.Uefi)
                ? PartitionStyleType.Gpt
                : PartitionStyleType.Mbr
        };

        var partitions = new List<PartitionSnapshot>();
        if (rawLayout is not null)
        {
            foreach (RawPartitionLayoutEntry rp in rawLayout.Partitions.OrderBy(p => p.StartingOffsetBytes))
            {
                if (rp.IsMbrExtendedContainer)
                {
                    // Extended MBR container (0x05 / 0x0F) wraps logical drives; primary slot usage is tracked in MbrPrimarySlotsUsed
                    continue;
                }

                RawVolumeBinding? mv = mountedVolumes.FirstOrDefault(v =>
                    v.DiskNumber == diskNum &&
                    ((v.StartingOffsetBytes > 0 && Math.Abs(v.StartingOffsetBytes - rp.StartingOffsetBytes) < 65536) ||
                     (v.PartitionNumber > 0 && v.PartitionNumber == rp.PartitionNumber)));

                string? dl = mv?.DriveLetter;
                if (dl is null && diskNum == targetDiskNumber &&
                    ((targetStartOffset > 0 && Math.Abs(rp.StartingOffsetBytes - targetStartOffset) < 65536) ||
                     rp.PartitionNumber == targetPartNumber))
                {
                    dl = sysDriveLetter;
                }

                if (diskNum == targetDiskNumber &&
                    string.Equals(dl, sysDriveLetter, StringComparison.OrdinalIgnoreCase) &&
                    rp.PartitionNumber > 0)
                {
                    targetPartNumber = rp.PartitionNumber;
                }

                string label = mv?.Label ?? string.Empty;
                string fs = mv?.FileSystem ?? string.Empty;
                long freeSpace = mv?.FreeBytes ?? 0;

                if (!string.IsNullOrWhiteSpace(dl) && string.IsNullOrWhiteSpace(fs))
                {
                    try
                    {
                        var di = new DriveInfo($"{dl}:");
                        if (di.IsReady)
                        {
                            label = di.VolumeLabel;
                            fs = di.DriveFormat;
                            freeSpace = di.AvailableFreeSpace;
                        }
                    }
                    catch
                    {
                        // Ignore unready DriveInfo
                    }
                }

                bool hasManifest = false;
                if (!string.IsNullOrWhiteSpace(dl))
                {
                    try
                    {
                        hasManifest = File.Exists($@"{dl}:\{SwbiBrand.ManifestFileName}") ||
                                      File.Exists($@"{dl}:\{SwbiBrand.LegacyManifestFileName}");
                    }
                    catch
                    {
                        // Ignore inaccessible root check
                    }
                }

                string gptTypeStr = partStyle == PartitionStyleType.Gpt && rp.GptPartitionType != Guid.Empty
                    ? $"{{{rp.GptPartitionType:D}}}"
                    : string.Empty;
                string partGuidStr = partStyle == PartitionStyleType.Gpt && rp.GptPartitionId != Guid.Empty
                    ? $"{{{rp.GptPartitionId:D}}}"
                    : partStyle == PartitionStyleType.Mbr
                        ? $"MBR:{rawLayout.MbrSignature:X8}:{rp.StartingOffsetBytes:X12}"
                        : string.Empty;

                bool isSysPart = string.Equals(gptTypeStr, GptPartitionTypeGuids.EfiSystem, StringComparison.OrdinalIgnoreCase) ||
                                 (partStyle == PartitionStyleType.Mbr && rp.MbrBootIndicator);
                bool isCurrentOs = diskNum == targetDiskNumber &&
                    (rp.PartitionNumber == targetPartNumber ||
                     string.Equals(dl, sysDriveLetter, StringComparison.OrdinalIgnoreCase));

                PartitionRole role = ClassifyPartitionRole(
                    isCurrentOs,
                    partStyle,
                    gptTypeStr,
                    rp.MbrPartitionType,
                    label,
                    hasManifest,
                    isSysPart,
                    rp.MbrBootIndicator);

                int normalizedPartNum = rp.PartitionNumber > 0 ? rp.PartitionNumber : partitions.Count + 1;
                partitions.Add(new PartitionSnapshot
                {
                    DiskNumber = diskNum,
                    PartitionNumber = normalizedPartNum,
                    PartitionGuid = partGuidStr,
                    GptTypeGuid = gptTypeStr,
                    DriveLetter = dl,
                    Label = label,
                    FileSystem = fs,
                    OffsetBytes = rp.StartingOffsetBytes,
                    SizeBytes = rp.PartitionLengthBytes,
                    FreeBytes = freeSpace,
                    Role = role,
                    IsBoot = isCurrentOs,
                    IsSystem = isSysPart,
                    IsActiveLegacy = rp.MbrBootIndicator
                });
            }
        }

        if (diskLengthBytes <= 0 && partitions.Count > 0)
        {
            diskLengthBytes = partitions.Max(p => p.OffsetBytes + p.SizeBytes);
        }

        long largestFreeExtent = ComputeLargestUnallocatedExtentBytes(diskLengthBytes, partitions);

        (string ctrlSvc, string ctrlName) = controllerMap.TryGetValue(diskNum, out var ci)
            ? ci
            : InferDefaultController(busTypeStr);

        StorageDriveKind driveKind = SmartCompatibilityAnalyzer.ClassifyDriveKind(
            busTypeStr,
            mediaTypeStr,
            ctrlSvc,
            friendlyName);

        string uniqueId = rawLayout is { PartitionStyle: Win32StorageInterop.PARTITION_STYLE_GPT } && rawLayout.GptDiskId != Guid.Empty
            ? rawLayout.GptDiskId.ToString("D")
            : rawLayout is { PartitionStyle: Win32StorageInterop.PARTITION_STYLE_MBR } && rawLayout.MbrSignature != 0
                ? $"MBR-{rawLayout.MbrSignature:X8}"
                : !string.IsNullOrWhiteSpace(hwProps.SerialNumber)
                    ? hwProps.SerialNumber
                    : $"Disk-{diskNum}";

        return new DiskSnapshot
        {
            DiskNumber = diskNum,
            FriendlyName = friendlyName,
            SerialNumber = hwProps.SerialNumber,
            UniqueId = uniqueId,
            BusType = busTypeStr,
            MediaTypeRaw = mediaTypeStr,
            DriveKind = driveKind,
            LogicalSectorBytes = hwProps.LogicalSectorBytes,
            PhysicalSectorBytes = hwProps.PhysicalSectorBytes,
            SupportsTrim = hwProps.SupportsTrim ?? (driveKind is StorageDriveKind.NvmeSsd or StorageDriveKind.SataSsd or StorageDriveKind.RaidOrVmd),
            ControllerServiceName = ctrlSvc,
            ControllerDisplayName = ctrlName,
            PartitionStyle = partStyle,
            MbrPrimarySlotsUsed = rawLayout?.MbrPrimarySlotsUsed ?? 0,
            HasMbrExtendedPartition = rawLayout?.HasMbrExtendedPartition ?? false,
            MbrSignature = rawLayout?.MbrSignature ?? 0,
            SizeBytes = diskLengthBytes,
            LargestFreeExtentBytes = largestFreeExtent,
            Health = DiskHealthState.Healthy,
            OperationalStatus = "Online",
            IsSystemDisk = diskNum == targetDiskNumber,
            IsBootDisk = diskNum == targetDiskNumber,
            IsRemovable = hwProps.IsRemovableMedia || busTypeStr.Equals("USB", StringComparison.OrdinalIgnoreCase),
            Partitions = partitions
        };
    }

    public static long ComputeLargestUnallocatedExtentBytes(
        long diskSizeBytes,
        IReadOnlyList<PartitionSnapshot> partitions)
    {
        if (diskSizeBytes <= 2 * 1024 * 1024)
        {
            return 0;
        }

        const long startReserve = 1024L * 1024L;
        long usableEnd = diskSizeBytes - (1024L * 1024L);
        long cursor = startReserve;
        long largest = 0;

        foreach (PartitionSnapshot p in partitions.OrderBy(x => x.OffsetBytes))
        {
            if (p.OffsetBytes > cursor)
            {
                long gap = p.OffsetBytes - cursor;
                if (gap > largest)
                {
                    largest = gap;
                }
            }

            long pEnd = p.OffsetBytes + p.SizeBytes;
            if (pEnd > cursor)
            {
                cursor = pEnd;
            }
        }

        if (usableEnd > cursor)
        {
            long tailGap = usableEnd - cursor;
            if (tailGap > largest)
            {
                largest = tailGap;
            }
        }

        return largest;
    }

    private static DiskSnapshot? BuildFallbackSystemDiskFromDriveInfo(
        string sysDriveLetter,
        int targetDiskNumber,
        int targetPartNumber,
        FirmwareMode firmwareMode)
    {
        try
        {
            var di = new DriveInfo($"{sysDriveLetter}:");
            if (!di.IsReady) return null;

            PartitionStyleType style = firmwareMode == FirmwareMode.Uefi ? PartitionStyleType.Gpt : PartitionStyleType.Mbr;
            var osPart = new PartitionSnapshot
            {
                DiskNumber = targetDiskNumber,
                PartitionNumber = Math.Max(1, targetPartNumber),
                DriveLetter = sysDriveLetter,
                Label = di.VolumeLabel,
                FileSystem = di.DriveFormat,
                OffsetBytes = 100L * 1024 * 1024,
                SizeBytes = di.TotalSize,
                FreeBytes = di.AvailableFreeSpace,
                Role = PartitionRole.WindowsOs,
                IsBoot = true
            };

            return new DiskSnapshot
            {
                DiskNumber = targetDiskNumber,
                FriendlyName = "System Disk",
                UniqueId = $"SystemDisk-{targetDiskNumber}",
                BusType = "SATA",
                MediaTypeRaw = "SSD",
                DriveKind = StorageDriveKind.SataSsd,
                PartitionStyle = style,
                SizeBytes = di.TotalSize + (128L * 1024 * 1024),
                LargestFreeExtentBytes = 0,
                Health = DiskHealthState.Healthy,
                OperationalStatus = "Online",
                IsSystemDisk = true,
                IsBootDisk = true,
                Partitions = new[] { osPart }
            };
        }
        catch
        {
            return null;
        }
    }

    private static CurrentWindowsInfo QueryCurrentWindowsFromRegistry(string sysDriveLetter)
    {
        string prodName = "Windows";
        string editionId = "Professional";
        string displayVersion = string.Empty;
        int buildNumber = Environment.OSVersion.Version.Build;

        try
        {
            using RegistryKey? ntCur = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (ntCur is not null)
            {
                prodName = ntCur.GetValue("ProductName") as string ?? prodName;
                editionId = ntCur.GetValue("EditionID") as string ?? editionId;
                displayVersion = ntCur.GetValue("DisplayVersion") as string
                    ?? ntCur.GetValue("ReleaseId") as string
                    ?? string.Empty;

                if (ntCur.GetValue("CurrentBuildNumber") is string bStr && int.TryParse(bStr, out int parsedBuild))
                {
                    buildNumber = parsedBuild;
                }
            }
        }
        catch
        {
            // Keep Environment.OSVersion defaults
        }

        if (buildNumber >= 22000 && prodName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            prodName = prodName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }

        string uiLang = System.Globalization.CultureInfo.InstalledUICulture.Name;
        if (string.IsNullOrWhiteSpace(uiLang))
        {
            uiLang = "en-US";
        }

        bool hasWindowsOld = Directory.Exists($@"{sysDriveLetter}:\Windows.old\Windows");

        return new CurrentWindowsInfo
        {
            ProductName = prodName,
            EditionId = editionId,
            DisplayVersion = displayVersion,
            BuildNumber = buildNumber,
            InstallLanguage = uiLang,
            CanRollbackToPreviousOs = hasWindowsOld
        };
    }

    private static PcHardwareCapabilities QueryHardwareCapabilitiesNative()
    {
        string cpuName = "Processor";
        int logicalCores = Math.Max(1, Environment.ProcessorCount);

        try
        {
            using RegistryKey? cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (cpuKey?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
            {
                cpuName = name.Trim();
            }
        }
        catch
        {
            // Ignore registry read error
        }

        long totalRamBytes = 0;
        var mem = new Win32StorageInterop.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<Win32StorageInterop.MEMORYSTATUSEX>()
        };
        if (Win32StorageInterop.GlobalMemoryStatusEx(ref mem))
        {
            totalRamBytes = (long)mem.ullTotalPhys;
        }

        bool tpmPresent = Win32StorageInterop.TryDetectTpmHardware(out string tpmVersion);
        if (!tpmPresent)
        {
            try
            {
                using RegistryKey? tpmReg = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TPM\WMI");
                if (tpmReg is not null)
                {
                    tpmPresent = true;
                    tpmVersion = "2.0";
                }
            }
            catch
            {
                // Ignore
            }
        }

        return new PcHardwareCapabilities
        {
            CpuName = cpuName,
            LogicalCores = logicalCores,
            TotalRamBytes = totalRamBytes,
            TpmPresent = tpmPresent,
            TpmVersion = tpmVersion,
            CpuSupports64Bit = DetectCpuSupports64Bit()
        };
    }

    public static bool DetectCpuSupports64Bit()
    {
        if (Environment.Is64BitOperatingSystem ||
            RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64)
        {
            return true;
        }

        // Running on 32-bit (x86) Windows: query CPU silicon Long Mode bit (CPUID 0x80000001.EDX[29]) directly!
        try
        {
            if (System.Runtime.Intrinsics.X86.X86Base.IsSupported)
            {
                (int maxExtLeaf, _, _, _) = System.Runtime.Intrinsics.X86.X86Base.CpuId(unchecked((int)0x80000000), 0);
                if ((uint)maxExtLeaf >= 0x80000001u)
                {
                    (_, _, _, int edx) = System.Runtime.Intrinsics.X86.X86Base.CpuId(unchecked((int)0x80000001), 0);
                    return (edx & (1 << 29)) != 0; // Bit 29 = LM (x86-64 / AMD64 / EM64T Long Mode)
                }
            }
        }
        catch
        {
            // Fallback to Registry Identifier string
        }

        try
        {
            using RegistryKey? cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string ident = cpuKey?.GetValue("Identifier") as string ?? string.Empty;
            if (ident.Contains("Intel64", StringComparison.OrdinalIgnoreCase) ||
                ident.Contains("AMD64", StringComparison.OrdinalIgnoreCase) ||
                ident.Contains("EM64T", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // Ignore
        }

        return false;
    }

    private static BitLockerStatusInfo QueryBitLockerStatusNative(string sysDriveLetter)
    {
        try
        {
            using SafeFileHandle vh = Win32StorageInterop.CreateFileW(
                $@"\\.\{sysDriveLetter}:",
                Win32StorageInterop.GENERIC_READ,
                Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
                IntPtr.Zero,
                Win32StorageInterop.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (!vh.IsInvalid)
            {
                using var fs = new FileStream(vh, FileAccess.Read, bufferSize: 4096, isAsync: false);
                byte[] bootSector = new byte[512];
                int read = fs.Read(bootSector, 0, bootSector.Length);
                if (read >= 11)
                {
                    string oemId = Encoding.ASCII.GetString(bootSector, 3, 8);
                    if (oemId.StartsWith("-FVE-FS-", StringComparison.Ordinal))
                    {
                        return new BitLockerStatusInfo
                        {
                            VolumeLetter = sysDriveLetter,
                            ProtectionState = BitLockerProtectionState.On,
                            ConversionState = BitLockerConversionState.FullyEncrypted,
                            HasRecoveryPasswordProtector = true,
                            KeyProtectorSummary = "BitLocker FVE Volume Header (-FVE-FS-)"
                        };
                    }

                    if (oemId.StartsWith("NTFS", StringComparison.Ordinal))
                    {
                        return new BitLockerStatusInfo
                        {
                            VolumeLetter = sysDriveLetter,
                            ProtectionState = BitLockerProtectionState.Off,
                            ConversionState = BitLockerConversionState.FullyDecrypted,
                            HasRecoveryPasswordProtector = false,
                            KeyProtectorSummary = string.Empty
                        };
                    }
                }
            }
        }
        catch
        {
            // Non-elevated or locked; fall through to Registry BitLockerStatus
        }

        try
        {
            using RegistryKey? blKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\BitLockerStatus");
            if (blKey?.GetValue("BootStatus") is int bootStat)
            {
                return new BitLockerStatusInfo
                {
                    VolumeLetter = sysDriveLetter,
                    ProtectionState = bootStat != 0 ? BitLockerProtectionState.On : BitLockerProtectionState.Off,
                    ConversionState = bootStat != 0 ? BitLockerConversionState.FullyEncrypted : BitLockerConversionState.FullyDecrypted,
                    HasRecoveryPasswordProtector = bootStat != 0,
                    KeyProtectorSummary = bootStat != 0 ? "BitLocker Active (Registry BootStatus)" : string.Empty
                };
            }
        }
        catch
        {
            // Ignore
        }

        return new BitLockerStatusInfo
        {
            VolumeLetter = sysDriveLetter,
            ProtectionState = BitLockerProtectionState.Off,
            ConversionState = BitLockerConversionState.FullyDecrypted,
            HasRecoveryPasswordProtector = false,
            KeyProtectorSummary = string.Empty
        };
    }

    private static bool? QueryNativeSecureBootState(FirmwareMode firmwareMode)
    {
        if (firmwareMode == FirmwareMode.LegacyBios)
        {
            return false;
        }

        // Priority 1: Direct UEFI GlobalVariable NVRAM 'SecureBoot' ({8be4df61-93ca-11d2-aa0d-00e098032b8c})
        // Works even if Registry keys were stripped/corrupted on a modded Windows OS!
        try
        {
            Win32StorageInterop.EnableProcessPrivilege("SeSystemEnvironmentPrivilege");
            byte[] sbBuf = new byte[4];
            uint nRead = Win32StorageInterop.GetFirmwareEnvironmentVariableExW(
                "SecureBoot",
                "{8be4df61-93ca-11d2-aa0d-00e098032b8c}",
                sbBuf,
                (uint)sbBuf.Length,
                out _);

            if (nRead >= 1)
            {
                return sbBuf[0] == 1;
            }
        }
        catch
        {
            // Fall through to Registry state
        }

        try
        {
            using RegistryKey? sbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (sbKey?.GetValue("UEFISecureBootEnabled") is int val)
            {
                return val == 1;
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    private static Dictionary<int, (string ServiceName, string ControllerName)> QueryStorageControllersFromRegistry(
        IEnumerable<int> diskIndices)
    {
        var result = new Dictionary<int, (string, string)>();

        string[] candidateServices =
        {
            "iaStorVD", "iaStorAC", "iaStorA", "rcraid", "rcbottom",
            "secnvme", "nvme", "stornvme", "storahci", "storufs",
            "viostor", "vioscsi", "lsi_sas", "percsas3", "vhdmp"
        };

        var activeServices = new List<(string Service, string DisplayName)>();
        try
        {
            foreach (string svc in candidateServices)
            {
                using RegistryKey? svcKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc}");
                using RegistryKey? enumKey = svcKey?.OpenSubKey("Enum");
                if (enumKey?.GetValue("Count") is int cnt && cnt > 0)
                {
                    string disp = CleanMuiDisplayString(svcKey?.GetValue("DisplayName") as string, svc);
                    activeServices.Add((svc, disp));
                }
            }

            using RegistryKey? diskEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\disk\Enum");
            foreach (int dIdx in diskIndices)
            {
                string pnpInstance = diskEnum?.GetValue(dIdx.ToString()) as string ?? string.Empty;
                (string svc, string disp) matched = MatchDiskPnpToActiveService(pnpInstance, activeServices);
                if (!string.IsNullOrEmpty(matched.svc))
                {
                    result[dIdx] = matched;
                }
            }
        }
        catch
        {
            // Ignore registry lookup errors
        }

        return result;
    }

    private static (string svc, string disp) MatchDiskPnpToActiveService(
        string diskPnpInstance,
        List<(string Service, string DisplayName)> activeServices)
    {
        foreach (string raidSvc in new[] { "iaStorVD", "iaStorAC", "iaStorA", "rcraid" })
        {
            var hit = activeServices.FirstOrDefault(s => s.Service.Equals(raidSvc, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(hit.Service) &&
                (diskPnpInstance.Contains("RAID", StringComparison.OrdinalIgnoreCase) ||
                 diskPnpInstance.Contains("NVME", StringComparison.OrdinalIgnoreCase) ||
                 diskPnpInstance.StartsWith("SCSI\\", StringComparison.OrdinalIgnoreCase)))
            {
                return hit;
            }
        }

        if (diskPnpInstance.Contains("NVME", StringComparison.OrdinalIgnoreCase))
        {
            var nvme = activeServices.FirstOrDefault(s => s.Service.Contains("nvme", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(nvme.Service)) return nvme;
            return ("stornvme", "Standard NVM Express Controller");
        }

        if (diskPnpInstance.Contains("VirtIO", StringComparison.OrdinalIgnoreCase) ||
            diskPnpInstance.Contains("RedHat", StringComparison.OrdinalIgnoreCase))
        {
            var vio = activeServices.FirstOrDefault(s => s.Service.StartsWith("vio", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(vio.Service)) return vio;
        }

        if (activeServices.Count > 0)
        {
            return activeServices[0];
        }

        return (string.Empty, string.Empty);
    }

    private static (string, string) InferDefaultController(string busType) =>
        busType.ToUpperInvariant() switch
        {
            "NVME" => ("stornvme", "Standard NVM Express Controller"),
            "SATA" or "ATA" => ("storahci", "Standard SATA AHCI Controller"),
            "RAID" => ("iaStorVD", "RAID / VMD Storage Controller"),
            "FILEBACKEDVIRTUAL" or "VIRTUAL" => ("vhdmp", "Microsoft Virtual Disk Controller"),
            _ => ("disk", "Standard Storage Controller")
        };

    private static string CleanMuiDisplayString(string? raw, string fallbackSvc)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallbackSvc;
        int semi = raw.LastIndexOf(';');
        if (semi >= 0 && semi + 1 < raw.Length)
        {
            return raw[(semi + 1)..].Trim();
        }
        return raw.Trim();
    }

    private static PartitionRole ClassifyPartitionRole(
        bool isCurrentOsPartition,
        PartitionStyleType partitionStyle,
        string gptType,
        byte mbrType,
        string label,
        bool hasInstallerManifest,
        bool isSystem,
        bool isActiveLegacy)
    {
        if (isCurrentOsPartition)
        {
            return PartitionRole.WindowsOs;
        }

        if (hasInstallerManifest || SwbiBrand.IsRecognizedPartitionLabel(label))
        {
            return PartitionRole.TemporaryInstaller;
        }

        if (string.Equals(gptType, GptPartitionTypeGuids.EfiSystem, StringComparison.OrdinalIgnoreCase))
        {
            return PartitionRole.EfiSystem;
        }

        if (string.Equals(gptType, GptPartitionTypeGuids.MicrosoftReserved, StringComparison.OrdinalIgnoreCase))
        {
            return PartitionRole.MicrosoftReserved;
        }

        if (string.Equals(gptType, GptPartitionTypeGuids.WindowsRecovery, StringComparison.OrdinalIgnoreCase) ||
            (partitionStyle == PartitionStyleType.Mbr && mbrType == 0x27))
        {
            return PartitionRole.Recovery;
        }

        if (partitionStyle == PartitionStyleType.Mbr && (isSystem || isActiveLegacy))
        {
            return PartitionRole.SystemReservedLegacy;
        }

        return PartitionRole.Data;
    }

    private static FirmwareMode QueryNativeFirmwareMode()
    {
        try
        {
            if (GetFirmwareType(out uint firmwareType))
            {
                return firmwareType switch
                {
                    1 => FirmwareMode.LegacyBios,
                    2 => FirmwareMode.Uefi,
                    _ => FirmwareMode.Unknown
                };
            }
        }
        catch
        {
            // Fallback to Unknown if API unavailable
        }

        return FirmwareMode.Unknown;
    }

    private static PowerStatusInfo QueryNativePowerStatus()
    {
        try
        {
            if (GetSystemPowerStatus(out SystemPowerStatusNative sps))
            {
                bool onAc = sps.ACLineStatus == 1;
                bool hasBattery = (sps.BatteryFlag & 128) == 0 && sps.BatteryFlag != 255;
                int? pct = sps.BatteryLifePercent <= 100 ? sps.BatteryLifePercent : null;
                return new PowerStatusInfo
                {
                    IsOnAcPower = onAc || !hasBattery,
                    HasBattery = hasBattery,
                    BatteryChargePercent = pct
                };
            }
        }
        catch
        {
            // Default to AC desktop if native power query fails
        }

        return new PowerStatusInfo
        {
            IsOnAcPower = true,
            HasBattery = false,
            BatteryChargePercent = null
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out uint firmwareType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatusNative lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatusNative
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
