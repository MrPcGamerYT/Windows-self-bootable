namespace UsbFreeInstaller.Core.Models;

public enum FirmwareMode
{
    Unknown = 0,
    LegacyBios = 1,
    Uefi = 2
}

public enum PartitionStyleType
{
    Unknown = 0,
    Mbr = 1,
    Gpt = 2,
    Raw = 3
}

public enum PartitionRole
{
    Unknown = 0,
    EfiSystem = 1,
    MicrosoftReserved = 2,
    WindowsOs = 3,
    Recovery = 4,
    TemporaryInstaller = 5,
    Data = 6,
    SystemReservedLegacy = 7
}

public enum DiskHealthState
{
    Unknown = 0,
    Healthy = 1,
    Warning = 2,
    Unhealthy = 3
}

public enum StorageDriveKind
{
    Unknown = 0,
    Hdd = 1,
    SataSsd = 2,
    NvmeSsd = 3,
    RaidOrVmd = 4,
    EmmcOrUfs = 5,
    VirtualDisk = 6
}

public static class GptPartitionTypeGuids
{
    public const string EfiSystem = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    public const string MicrosoftReserved = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}";
    public const string BasicData = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
    public const string WindowsRecovery = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";
}

public static class ByteFormatting
{
    public const long OneMb = 1024L * 1024L;
    public const long OneGb = 1024L * 1024L * 1024L;

    public static string FormatGb(long bytes)
    {
        if (bytes < OneGb)
        {
            double mb = bytes / (double)OneMb;
            return $"{mb:0.#} MB";
        }

        double gb = bytes / (double)OneGb;
        return $"{gb:0.0} GB";
    }
}

public sealed record PartitionSnapshot
{
    public required int DiskNumber { get; init; }
    public required int PartitionNumber { get; init; }
    public string PartitionGuid { get; init; } = string.Empty;
    public string GptTypeGuid { get; init; } = string.Empty;
    public string? DriveLetter { get; init; }
    public string Label { get; init; } = string.Empty;
    public string FileSystem { get; init; } = string.Empty;
    public required long OffsetBytes { get; init; }
    public required long SizeBytes { get; init; }
    public long FreeBytes { get; init; }
    public PartitionRole Role { get; init; } = PartitionRole.Unknown;
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public bool IsActiveLegacy { get; init; }

    public long EndOffsetBytes => OffsetBytes + SizeBytes;

    public string SizeDisplay => ByteFormatting.FormatGb(SizeBytes);

    public string FreeDisplay => FreeBytes > 0 ? ByteFormatting.FormatGb(FreeBytes) : "—";

    public string SetupDisplayName =>
        string.IsNullOrWhiteSpace(Label)
            ? $"Disk {DiskNumber} Partition {PartitionNumber}"
            : $"Disk {DiskNumber} Partition {PartitionNumber}: {Label}";
}

public sealed record ShrinkAnalysis
{
    public required long CurrentSizeBytes { get; init; }
    public required long MinimumSupportedSizeBytes { get; init; }
    public required long VolumeFreeBytes { get; init; }
    public required long OsSafetyReserveBytes { get; init; }

    /// <summary>
    /// Raw shrink ceiling reported by Windows Get-PartitionSupportedSize (CurrentSize - SizeMin).
    /// Can be smaller than VolumeFreeBytes when unmovable MFT/pagefile/hiberfil blocks sit near the partition end.
    /// </summary>
    public long RawMaxShrinkableBytes => Math.Max(0L, CurrentSizeBytes - MinimumSupportedSizeBytes);

    /// <summary>
    /// Bytes blocked from shrinking due to unmovable system clusters near the end of the partition.
    /// </summary>
    public long UnmovableTailBytes => Math.Max(0L, VolumeFreeBytes - RawMaxShrinkableBytes);

    /// <summary>
    /// Maximum shrink bytes while still leaving OsSafetyReserveBytes of free space on the active OS volume.
    /// </summary>
    public long SafeUsableShrinkBytes =>
        Math.Max(0L, Math.Min(RawMaxShrinkableBytes, VolumeFreeBytes - OsSafetyReserveBytes));
}

public sealed record DiskSnapshot
{
    private static readonly HashSet<string> InboxWindowsStorageServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "",
        "disk",
        "stornvme",
        "storahci",
        "pciide",
        "intelide",
        "atapi",
        "spaceport",
        "storvsc",
        "sdbus",
        "storahci_ms",
        "usbstor",
        "uaspstor",
        "vhdmp"
    };

    public required int DiskNumber { get; init; }
    public required string FriendlyName { get; init; }
    public string SerialNumber { get; init; } = string.Empty;
    public string UniqueId { get; init; } = string.Empty;
    public string BusType { get; init; } = "Unknown";
    public string MediaTypeRaw { get; init; } = "Unspecified";
    public StorageDriveKind DriveKind { get; init; } = StorageDriveKind.Unknown;
    public int LogicalSectorBytes { get; init; } = 512;
    public int PhysicalSectorBytes { get; init; } = 4096;
    public bool SupportsTrim { get; init; }
    public string ControllerServiceName { get; init; } = string.Empty;
    public string ControllerDisplayName { get; init; } = string.Empty;
    public PartitionStyleType PartitionStyle { get; init; } = PartitionStyleType.Unknown;
    public int MbrPrimarySlotsUsed { get; init; }
    public bool HasMbrExtendedPartition { get; init; }
    public uint MbrSignature { get; init; }
    public required long SizeBytes { get; init; }
    public long LargestFreeExtentBytes { get; init; }
    public DiskHealthState Health { get; init; } = DiskHealthState.Unknown;
    public string OperationalStatus { get; init; } = "OK";
    public bool IsSystemDisk { get; init; }
    public bool IsBootDisk { get; init; }
    public bool IsRemovable { get; init; }
    public IReadOnlyList<PartitionSnapshot> Partitions { get; init; } = Array.Empty<PartitionSnapshot>();

    public string SizeDisplay => ByteFormatting.FormatGb(SizeBytes);

    /// <summary>
    /// Number of primary MBR table slots (0..4) currently occupied on an MBR disk (including any 0x05/0x0F Extended container).
    /// </summary>
    public int EffectiveMbrPrimarySlotsUsed =>
        PartitionStyle != PartitionStyleType.Mbr
            ? 0
            : MbrPrimarySlotsUsed > 0
                ? Math.Min(4, MbrPrimarySlotsUsed)
                : Math.Min(4, Partitions.Count);

    /// <summary>
    /// True when an MBR disk exceeds the 32-bit LBA ceiling (~2 TiB for 512B sectors).
    /// </summary>
    public bool ExceedsMbr2TibLimit =>
        PartitionStyle == PartitionStyleType.Mbr &&
        SizeBytes > 0xFFFF_FFFFL * Math.Max(512, LogicalSectorBytes);

    /// <summary>
    /// True when the OS disk depends on an OEM/third-party storage controller driver (e.g. Intel VMD/RST iaStorVD,
    /// AMD RAID rcraid, VirtIO vioscsi, VMware pvscsi) that vanilla Microsoft ISOs do not bundle inside boot.wim.
    /// </summary>
    public bool RequiresWinPeDriverInjection =>
        DriveKind == StorageDriveKind.RaidOrVmd ||
        (!string.IsNullOrWhiteSpace(ControllerServiceName) &&
         !InboxWindowsStorageServices.Contains(ControllerServiceName.Trim()));

    public string DriveKindDisplay => DriveKind switch
    {
        StorageDriveKind.NvmeSsd => "NVMe PCIe SSD",
        StorageDriveKind.SataSsd => "SATA / AHCI SSD",
        StorageDriveKind.Hdd => "Mechanical Hard Drive (HDD)",
        StorageDriveKind.RaidOrVmd => "Intel VMD / Hardware RAID Storage",
        StorageDriveKind.EmmcOrUfs => "Embedded Flash (eMMC / UFS)",
        StorageDriveKind.VirtualDisk => "Virtual Machine Disk",
        _ => string.IsNullOrWhiteSpace(BusType) ? "Internal Drive" : $"{BusType} Drive"
    };

    public string SectorGeometryDisplay =>
        LogicalSectorBytes == 4096
            ? "4Kn Native (4096B)"
            : PhysicalSectorBytes == 4096
                ? "512e Advanced Format (512B/4096B)"
                : $"{LogicalSectorBytes}B Sectors";
}

public sealed record SystemEnvironmentSnapshot
{
    public required bool IsElevated { get; init; }
    public required FirmwareMode Firmware { get; init; }
    public required bool? SecureBootEnabled { get; init; }
    public required string HostArchitecture { get; init; }
    public required string SystemDriveLetter { get; init; }
    public required int TargetDiskNumber { get; init; }
    public required int TargetOsPartitionNumber { get; init; }
    public required IReadOnlyList<DiskSnapshot> AllDisks { get; init; }
    public ShrinkAnalysis? ShrinkInfo { get; init; }
    public BitLockerStatusInfo? BitLocker { get; init; }
    public PowerStatusInfo? PowerStatus { get; init; }
    public CurrentWindowsInfo? CurrentWindows { get; init; }
    public PcHardwareCapabilities? Hardware { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool IsSimulated { get; init; }
    public string? SimulationProfileName { get; init; }

    public DiskSnapshot? TargetDisk =>
        AllDisks.FirstOrDefault(d => d.DiskNumber == TargetDiskNumber);

    public PartitionSnapshot? TargetOsPartition =>
        TargetDisk?.Partitions.FirstOrDefault(p => p.PartitionNumber == TargetOsPartitionNumber);

    public int InternalFixedDiskCount =>
        AllDisks.Count(d => !d.IsRemovable && !string.Equals(d.BusType, "USB", StringComparison.OrdinalIgnoreCase));
}
