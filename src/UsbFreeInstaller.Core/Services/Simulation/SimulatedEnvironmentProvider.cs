using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Services.Simulation;

public sealed record SimulationScenario(
    string Id,
    string DisplayName,
    string Description,
    SystemEnvironmentSnapshot Environment,
    IsoInspectionResult SampleIso,
    InstallerManifest? CleanupManifest = null);

/// <summary>
/// Built-in hardware, drive-type (NVMe, SATA SSD, Mechanical HDD, Intel VMD RAID), and
/// Upgrade / Downgrade ("Degrade") simulation scenarios for safe Dry-Run testing and automated tests.
/// </summary>
public static class SimulatedEnvironmentProvider
{
    public static IReadOnlyList<SimulationScenario> GetBuiltInScenarios() =>
        new[]
        {
            CreateHealthySingleDriveScenario(),
            CreateCustomTiny11OlderPcScenario(),
            CreateIntelVmdUpgradeWin10ToWin11Scenario(),
            CreateSataHddDowngradeWin11ToWin10Scenario(),
            CreateBitLockerLaptopScenario(),
            CreateLegacyBiosUnsupportedScenario(),
            CreatePostInstallAdjacentCleanupScenario(),
            CreatePostInstallRecoveryBlockedExtendScenario()
        };

    public static SimulationScenario CreateHealthySingleDriveScenario()
    {
        const long gb = ByteFormatting.OneGb;
        const long mb = ByteFormatting.OneMb;

        var efi = new PartitionSnapshot
        {
            DiskNumber = 0,
            PartitionNumber = 1,
            PartitionGuid = "{11111111-0001-4000-8000-000000000001}",
            GptTypeGuid = GptPartitionTypeGuids.EfiSystem,
            Label = "SYSTEM",
            FileSystem = "FAT32",
            OffsetBytes = 1L * mb,
            SizeBytes = 100L * mb,
            FreeBytes = 68L * mb,
            Role = PartitionRole.EfiSystem,
            IsSystem = true
        };

        var msr = new PartitionSnapshot
        {
            DiskNumber = 0,
            PartitionNumber = 2,
            PartitionGuid = "{11111111-0002-4000-8000-000000000002}",
            GptTypeGuid = GptPartitionTypeGuids.MicrosoftReserved,
            OffsetBytes = 101L * mb,
            SizeBytes = 16L * mb,
            Role = PartitionRole.MicrosoftReserved
        };

        var windowsC = new PartitionSnapshot
        {
            DiskNumber = 0,
            PartitionNumber = 3,
            PartitionGuid = "{11111111-0003-4000-8000-000000000003}",
            GptTypeGuid = GptPartitionTypeGuids.BasicData,
            DriveLetter = "C",
            Label = "Windows",
            FileSystem = "NTFS",
            OffsetBytes = 117L * mb,
            SizeBytes = 475L * gb,
            FreeBytes = 190L * gb,
            Role = PartitionRole.WindowsOs,
            IsBoot = true
        };

        var winRe = new PartitionSnapshot
        {
            DiskNumber = 0,
            PartitionNumber = 4,
            PartitionGuid = "{11111111-0004-4000-8000-000000000004}",
            GptTypeGuid = GptPartitionTypeGuids.WindowsRecovery,
            Label = "WinRE",
            FileSystem = "NTFS",
            OffsetBytes = (117L * mb) + (475L * gb),
            SizeBytes = 800L * mb,
            FreeBytes = 250L * mb,
            Role = PartitionRole.Recovery
        };

        var disk0 = new DiskSnapshot
        {
            DiskNumber = 0,
            FriendlyName = "Samsung SSD 990 PRO 512GB (Simulated NVMe)",
            SerialNumber = "SIM-NVME-0001",
            UniqueId = "eui.002538B411B00001",
            BusType = "NVMe",
            MediaTypeRaw = "SSD",
            DriveKind = StorageDriveKind.NvmeSsd,
            LogicalSectorBytes = 512,
            PhysicalSectorBytes = 4096,
            SupportsTrim = true,
            ControllerServiceName = "stornvme",
            ControllerDisplayName = "Standard NVM Express Controller",
            PartitionStyle = PartitionStyleType.Gpt,
            SizeBytes = 476L * gb,
            LargestFreeExtentBytes = 0L,
            Health = DiskHealthState.Healthy,
            OperationalStatus = "OK",
            IsSystemDisk = true,
            IsBootDisk = true,
            Partitions = new[] { efi, msr, windowsC, winRe }
        };

        var env = new SystemEnvironmentSnapshot
        {
            IsElevated = true,
            Firmware = FirmwareMode.Uefi,
            SecureBootEnabled = true,
            HostArchitecture = "x64",
            SystemDriveLetter = "C",
            TargetDiskNumber = 0,
            TargetOsPartitionNumber = 3,
            AllDisks = new[] { disk0 },
            ShrinkInfo = new ShrinkAnalysis
            {
                CurrentSizeBytes = 475L * gb,
                MinimumSupportedSizeBytes = 310L * gb,
                VolumeFreeBytes = 190L * gb,
                OsSafetyReserveBytes = PartitionSizingCalculator.DefaultOsSafetyReserveBytes
            },
            BitLocker = new BitLockerStatusInfo
            {
                VolumeLetter = "C",
                ProtectionState = BitLockerProtectionState.Off,
                ConversionState = BitLockerConversionState.FullyDecrypted,
                HasRecoveryPasswordProtector = false
            },
            PowerStatus = new PowerStatusInfo
            {
                IsOnAcPower = true,
                HasBattery = false
            },
            CurrentWindows = new CurrentWindowsInfo
            {
                ProductName = "Windows 11 Pro",
                EditionId = "Professional",
                DisplayVersion = "24H2",
                BuildNumber = 26100,
                InstallLanguage = "en-US"
            },
            Hardware = new PcHardwareCapabilities
            {
                CpuName = "AMD Ryzen 7 7840HS",
                LogicalCores = 16,
                TotalRamBytes = 16L * gb,
                TpmPresent = true,
                TpmVersion = "2.0"
            },
            IsSimulated = true,
            SimulationProfileName = "NVMe SSD PC — Clean Reinstall Windows 11 24H2"
        };

        return new SimulationScenario(
            Id: "healthy-single-drive",
            DisplayName: "1. NVMe SSD PC — Clean Reinstall Windows 11 24H2 (Same Build)",
            Description: "Modern PC with one 512 GB NVMe SSD (C: only), TPM 2.0, Secure Boot ON, and a 6.4 GB Windows 11 ISO.",
            Environment: env,
            SampleIso: CreateSampleWin11Iso());
    }

    public static SimulationScenario CreateIntelVmdUpgradeWin10ToWin11Scenario()
    {
        SimulationScenario baseScenario = CreateHealthySingleDriveScenario();
        DiskSnapshot vmdDisk = baseScenario.Environment.AllDisks[0] with
        {
            FriendlyName = "WD Black SN850X 1TB (Intel VMD Controller)",
            SizeBytes = 931L * ByteFormatting.OneGb,
            BusType = "RAID",
            MediaTypeRaw = "SSD",
            DriveKind = StorageDriveKind.RaidOrVmd,
            ControllerServiceName = "iaStorVD",
            ControllerDisplayName = "Intel RST VMD Controller 467F"
        };

        SystemEnvironmentSnapshot env = baseScenario.Environment with
        {
            AllDisks = new[] { vmdDisk },
            CurrentWindows = new CurrentWindowsInfo
            {
                ProductName = "Windows 10 Home",
                EditionId = "Core",
                DisplayVersion = "22H2",
                BuildNumber = 19045,
                InstallLanguage = "en-US"
            },
            Hardware = new PcHardwareCapabilities
            {
                CpuName = "13th Gen Intel Core i7-13700H",
                LogicalCores = 20,
                TotalRamBytes = 16L * ByteFormatting.OneGb,
                TpmPresent = true,
                TpmVersion = "2.0"
            },
            SimulationProfileName = "Intel VMD NVMe Laptop — Upgrade Win10 22H2 → Win11 24H2"
        };

        return new SimulationScenario(
            Id: "intel-vmd-upgrade",
            DisplayName: "2. Intel VMD NVMe Laptop — Smart Upgrade Win10 22H2 → Win11 24H2",
            Description: "Detects Intel VMD ('iaStorVD') controller, auto-injects storage drivers into boot.wim + $WinPEDriver$ so Setup sees C:, and enables Clean or In-Place Upgrade.",
            Environment: env,
            SampleIso: CreateSampleWin11Iso());
    }

    public static SimulationScenario CreateSataHddDowngradeWin11ToWin10Scenario()
    {
        const long gb = ByteFormatting.OneGb;
        SimulationScenario baseScenario = CreateHealthySingleDriveScenario();

        DiskSnapshot sataHdd = baseScenario.Environment.AllDisks[0] with
        {
            FriendlyName = "Seagate BarraCuda ST1000DM010 1TB 7200RPM (SATA HDD)",
            SizeBytes = 931L * gb,
            BusType = "SATA",
            MediaTypeRaw = "HDD",
            DriveKind = StorageDriveKind.Hdd,
            SupportsTrim = false,
            ControllerServiceName = "storahci",
            ControllerDisplayName = "Standard SATA AHCI Controller"
        };

        SystemEnvironmentSnapshot env = baseScenario.Environment with
        {
            AllDisks = new[] { sataHdd },
            ShrinkInfo = new ShrinkAnalysis
            {
                CurrentSizeBytes = 930L * gb,
                MinimumSupportedSizeBytes = 790L * gb,
                VolumeFreeBytes = 210L * gb, // 70 GB unmovable tail on HDD -> recommends pre-shrink HDD consolidation!
                OsSafetyReserveBytes = PartitionSizingCalculator.DefaultOsSafetyReserveBytes
            },
            SimulationProfileName = "1TB SATA Hard Drive (HDD) — Downgrade Win11 24H2 → Win10 22H2"
        };

        return new SimulationScenario(
            Id: "hdd-downgrade-win10",
            DisplayName: "3. Mechanical SATA HDD PC — Smart Downgrade ('Degrade') Win11 → Win10 22H2",
            Description: "Detects spinning SATA Hard Drive (4 MB sequential buffer, HDD tail defrag consolidation) and configures USB-free clean Downgrade from Windows 11 to Windows 10 22H2.",
            Environment: env,
            SampleIso: CreateSampleWin10Iso());
    }

    public static SimulationScenario CreateBitLockerLaptopScenario()
    {
        SimulationScenario baseScenario = CreateHealthySingleDriveScenario();
        DiskSnapshot sataSsd = baseScenario.Environment.AllDisks[0] with
        {
            FriendlyName = "Crucial MX500 500GB (SATA AHCI SSD)",
            BusType = "SATA",
            MediaTypeRaw = "SSD",
            DriveKind = StorageDriveKind.SataSsd,
            ControllerServiceName = "storahci"
        };

        SystemEnvironmentSnapshot env = baseScenario.Environment with
        {
            AllDisks = new[] { sataSsd },
            BitLocker = new BitLockerStatusInfo
            {
                VolumeLetter = "C",
                ProtectionState = BitLockerProtectionState.On,
                ConversionState = BitLockerConversionState.FullyEncrypted,
                HasRecoveryPasswordProtector = true,
                KeyProtectorSummary = "Tpm, RecoveryPassword"
            },
            PowerStatus = new PowerStatusInfo
            {
                IsOnAcPower = false,
                HasBattery = true,
                BatteryChargePercent = 68
            },
            SimulationProfileName = "SATA SSD BitLocker Laptop on Battery Power"
        };

        return new SimulationScenario(
            Id: "bitlocker-laptop",
            DisplayName: "4. SATA SSD BitLocker Laptop (Battery Warning + Recovery Key Gate)",
            Description: "Tests SATA SSD tuning, BitLocker recovery key gate, 1-reboot BitLocker suspension, and laptop AC power warning.",
            Environment: env,
            SampleIso: baseScenario.SampleIso);
    }

    public static SimulationScenario CreateLegacyBiosUnsupportedScenario()
    {
        SimulationScenario baseScenario = CreateHealthySingleDriveScenario();
        DiskSnapshot mbrDisk = baseScenario.Environment.AllDisks[0] with
        {
            PartitionStyle = PartitionStyleType.Mbr
        };

        SystemEnvironmentSnapshot env = baseScenario.Environment with
        {
            Firmware = FirmwareMode.LegacyBios,
            SecureBootEnabled = false,
            AllDisks = new[] { mbrDisk },
            SimulationProfileName = "Legacy BIOS / MBR PC with 4/4 Primary Slots Full"
        };

        return new SimulationScenario(
            Id: "legacy-mbr-blocked",
            DisplayName: "5. Legacy BIOS / MBR Disk Full (4/4 Primary Slots — Blocked Safely with mbr2gpt Guide)",
            Description: "Verifies that an MBR disk with all 4 primary slots already full is blocked safely before disk changes and guides mbr2gpt conversion.",
            Environment: env,
            SampleIso: baseScenario.SampleIso);
    }

    public static SimulationScenario CreatePostInstallAdjacentCleanupScenario()
    {
        const long gb = ByteFormatting.OneGb;
        const long mb = ByteFormatting.OneMb;
        const string installerGuid = "{99999999-ABCD-4000-8000-000000000099}";

        var efi = new PartitionSnapshot
        {
            DiskNumber = 0,
            PartitionNumber = 1,
            PartitionGuid = "{11111111-0001-4000-8000-000000000001}",
            GptTypeGuid = GptPartitionTypeGuids.EfiSystem,
            Label = "SYSTEM",
            FileSystem = "FAT32",
            OffsetBytes = 1L * mb,
            SizeBytes = 100L * mb,
            Role = PartitionRole.EfiSystem,
            IsSystem = true
        };

        var msr = new PartitionSnapshot
        {
            DiskNumber = 0,
            PartitionNumber = 2,
            PartitionGuid = "{11111111-0002-4000-8000-000000000002}",
            GptTypeGuid = GptPartitionTypeGuids.MicrosoftReserved,
            OffsetBytes = 101L * mb,
            SizeBytes = 16L * mb,
            Role = PartitionRole.MicrosoftReserved
        };

        var freshWindowsC = new PartitionSnapshot
        {
            DiskNumber = 0,
            PartitionNumber = 3,
            PartitionGuid = "{22222222-NEW0-4000-8000-000000000003}",
            GptTypeGuid = GptPartitionTypeGuids.BasicData,
            DriveLetter = "C",
            Label = "Windows",
            FileSystem = "NTFS",
            OffsetBytes = 117L * mb,
            SizeBytes = 466L * gb,
            FreeBytes = 432L * gb,
            Role = PartitionRole.WindowsOs,
            IsBoot = true
        };

        var winInstall = new PartitionSnapshot
        {
            DiskNumber = 0,
            PartitionNumber = 4,
            PartitionGuid = installerGuid,
            GptTypeGuid = GptPartitionTypeGuids.BasicData,
            DriveLetter = "D",
            Label = InstallerManifest.DefaultVolumeLabel,
            FileSystem = "FAT32",
            OffsetBytes = (117L * mb) + (466L * gb),
            SizeBytes = 9L * gb,
            FreeBytes = 2L * gb,
            Role = PartitionRole.TemporaryInstaller
        };

        var disk0 = new DiskSnapshot
        {
            DiskNumber = 0,
            FriendlyName = "Samsung SSD 990 PRO 512GB (Post-Install Adjacent)",
            UniqueId = "eui.002538B411B00001",
            BusType = "NVMe",
            MediaTypeRaw = "SSD",
            DriveKind = StorageDriveKind.NvmeSsd,
            PartitionStyle = PartitionStyleType.Gpt,
            SizeBytes = 476L * gb,
            Health = DiskHealthState.Healthy,
            OperationalStatus = "OK",
            IsSystemDisk = true,
            IsBootDisk = true,
            Partitions = new[] { efi, msr, freshWindowsC, winInstall }
        };

        var manifest = new InstallerManifest
        {
            SourceIsoFileName = "Win11_24H2_English_x64.iso",
            DiskNumber = 0,
            DiskUniqueId = disk0.UniqueId,
            DriveKind = StorageDriveKind.NvmeSsd,
            InstallerPartitionNumber = 4,
            InstallerPartitionGuid = installerGuid,
            InstallerPartitionBytes = winInstall.SizeBytes,
            VolumeLabel = SwbiBrand.PartitionVolumeLabel,
            SplitStrategy = WimSplitStrategy.SplitWimToSwm,
            BootFirmwareMode = FirmwareMode.Uefi,
            BcdFirmwareBootGuid = "UEFI:Boot0010"
        };

        var env = new SystemEnvironmentSnapshot
        {
            IsElevated = true,
            Firmware = FirmwareMode.Uefi,
            SecureBootEnabled = true,
            HostArchitecture = "x64",
            SystemDriveLetter = "C",
            TargetDiskNumber = 0,
            TargetOsPartitionNumber = 3,
            AllDisks = new[] { disk0 },
            IsSimulated = true,
            SimulationProfileName = "Post-Install Ready for Cleanup (Adjacent Space)"
        };

        return new SimulationScenario(
            Id: "cleanup-adjacent",
            DisplayName: $"6. Post-Install Cleanup — Adjacent {SwbiBrand.PartitionVolumeLabel} (Can Auto-Extend C:)",
            Description: $"Simulates a fresh Windows install where {SwbiBrand.PartitionVolumeLabel} sits directly to the right of C:, allowing cleanup + automatic C: expansion.",
            Environment: env,
            SampleIso: CreateSampleWin11Iso(),
            CleanupManifest: manifest);
    }

    public static SimulationScenario CreatePostInstallRecoveryBlockedExtendScenario()
    {
        const long gb = ByteFormatting.OneGb;
        const long mb = ByteFormatting.OneMb;

        SimulationScenario adj = CreatePostInstallAdjacentCleanupScenario();
        DiskSnapshot baseDisk = adj.Environment.AllDisks[0];

        var freshC = baseDisk.Partitions[2] with { SizeBytes = 465L * gb };
        var newWinRe = new PartitionSnapshot
        {
            DiskNumber = 0,
            PartitionNumber = 4,
            PartitionGuid = "{33333333-RECV-4000-8000-000000000004}",
            GptTypeGuid = GptPartitionTypeGuids.WindowsRecovery,
            Label = "Recovery",
            FileSystem = "NTFS",
            OffsetBytes = freshC.EndOffsetBytes,
            SizeBytes = 750L * mb,
            Role = PartitionRole.Recovery
        };
        var winInstall = baseDisk.Partitions[3] with
        {
            PartitionNumber = 5,
            OffsetBytes = newWinRe.EndOffsetBytes
        };

        DiskSnapshot updatedDisk = baseDisk with
        {
            FriendlyName = $"Samsung SSD 990 PRO 512GB (WinRE Between C: & {SwbiBrand.PartitionVolumeLabel})",
            Partitions = new[] { baseDisk.Partitions[0], baseDisk.Partitions[1], freshC, newWinRe, winInstall }
        };

        InstallerManifest updatedManifest = adj.CleanupManifest! with
        {
            InstallerPartitionNumber = 5
        };

        return new SimulationScenario(
            Id: "cleanup-intervening-winre",
            DisplayName: $"7. Post-Install Cleanup — WinRE Between C: & {SwbiBrand.PartitionVolumeLabel}",
            Description: $"Simulates when Windows Setup placed a new 750 MB Recovery partition between C: and {SwbiBrand.PartitionVolumeLabel}: safely deletes {SwbiBrand.PartitionVolumeLabel} without falsely promising automatic C: expansion.",
            Environment: adj.Environment with
            {
                AllDisks = new[] { updatedDisk },
                SimulationProfileName = "Post-Install Cleanup (Intervening WinRE)"
            },
            SampleIso: adj.SampleIso,
            CleanupManifest: updatedManifest);
    }

    public static IsoInspectionResult CreateSampleWin11Iso()
    {
        const long gb = ByteFormatting.OneGb;
        const long mb = ByteFormatting.OneMb;

        return new IsoInspectionResult
        {
            IsoPath = @"C:\Downloads\Win11_24H2_English_x64.iso",
            Exists = true,
            FileSizeBytes = (6L * gb) + (420L * mb),
            Sha256Hex = "b8426f10e9a14d0c7e522f1a8193a4d09b32145cf18c4a0d8f7e123456789abc",
            HasBootSdi = true,
            HasBootWim = true,
            HasEfiBootloader = true,
            HasSetupExe = true,
            Architecture = "x64",
            PayloadKind = InstallPayloadKind.Wim,
            PayloadRelativePath = @"sources\install.wim",
            PayloadSizeBytes = (5L * gb) + (280L * mb),
            SplitStrategy = WimSplitStrategy.SplitWimToSwm,
            SwmChunkSizeMb = IsoInspectionResult.DefaultSwmChunkSizeMb,
            Editions = new[]
            {
                new WimEditionInfo { Index = 1, Name = "Windows 11 Home", Architecture = "x64", Version = "10.0.26100", SizeBytes = 18L * gb },
                new WimEditionInfo { Index = 2, Name = "Windows 11 Education", Architecture = "x64", Version = "10.0.26100", SizeBytes = 19L * gb },
                new WimEditionInfo { Index = 3, Name = "Windows 11 Pro", Architecture = "x64", Version = "10.0.26100", SizeBytes = 19L * gb }
            }
        };
    }

    public static IsoInspectionResult CreateSampleWin10Iso()
    {
        const long gb = ByteFormatting.OneGb;
        const long mb = ByteFormatting.OneMb;

        return new IsoInspectionResult
        {
            IsoPath = @"C:\Downloads\Win10_22H2_English_x64.iso",
            Exists = true,
            FileSizeBytes = (5L * gb) + (750L * mb),
            Sha256Hex = "a1234f10e9a14d0c7e522f1a8193a4d09b32145cf18c4a0d8f7e9876543210ff",
            HasBootSdi = true,
            HasBootWim = true,
            HasEfiBootloader = true,
            HasSetupExe = true,
            Architecture = "x64",
            PayloadKind = InstallPayloadKind.Wim,
            PayloadRelativePath = @"sources\install.wim",
            PayloadSizeBytes = (4L * gb) + (620L * mb),
            SplitStrategy = WimSplitStrategy.SplitWimToSwm,
            SwmChunkSizeMb = IsoInspectionResult.DefaultSwmChunkSizeMb,
            Editions = new[]
            {
                new WimEditionInfo { Index = 1, Name = "Windows 10 Home", Architecture = "x64", Version = "10.0.19045", SizeBytes = 15L * gb },
                new WimEditionInfo { Index = 2, Name = "Windows 10 Education", Architecture = "x64", Version = "10.0.19045", SizeBytes = 16L * gb },
                new WimEditionInfo { Index = 3, Name = "Windows 10 Pro", Architecture = "x64", Version = "10.0.19045", SizeBytes = 16L * gb }
            }
        };
    }

    public static SimulationScenario CreateCustomTiny11OlderPcScenario()
    {
        const long gb = ByteFormatting.OneGb;
        const long mb = ByteFormatting.OneMb;

        SimulationScenario baseScenario = CreateHealthySingleDriveScenario();
        DiskSnapshot baseDisk = baseScenario.Environment.AllDisks[0] with
        {
            FriendlyName = "Crucial MX500 250GB SATA SSD (Simulated Older PC)",
            BusType = "SATA",
            DriveKind = StorageDriveKind.SataSsd,
            ControllerServiceName = "storahci",
            ControllerDisplayName = "Standard SATA AHCI Controller"
        };

        SystemEnvironmentSnapshot env = baseScenario.Environment with
        {
            SecureBootEnabled = false,
            AllDisks = new[] { baseDisk },
            CurrentWindows = new CurrentWindowsInfo
            {
                ProductName = "Windows 10 Pro",
                EditionId = "Professional",
                DisplayVersion = "22H2",
                BuildNumber = 19045,
                InstallLanguage = "en-US"
            },
            Hardware = new PcHardwareCapabilities
            {
                CpuName = "Intel Core i5-6500 @ 3.20GHz",
                LogicalCores = 4,
                TotalRamBytes = 3L * gb,
                TpmPresent = false,
                TpmVersion = "None"
            }
        };

        var customIso = new IsoInspectionResult
        {
            IsoPath = @"C:\Downloads\tiny11_24H2_x64_custom.iso",
            Exists = true,
            FileSizeBytes = (3L * gb) + (150L * mb),
            HasBootSdi = false,
            HasBootWim = true,
            HasEfiBootloader = true,
            HasSetupExe = false,
            IsCustomOrModifiedIso = true,
            Architecture = "x64",
            PayloadKind = InstallPayloadKind.Esd,
            PayloadRelativePath = @"sources\install.esd",
            PayloadSizeBytes = (2L * gb) + (750L * mb),
            SplitStrategy = WimSplitStrategy.DirectCopy,
            Editions = new[]
            {
                new WimEditionInfo
                {
                    Index = 1,
                    Name = "Tiny11 24H2 Core (Custom Lite)",
                    Architecture = "x64",
                    Version = "10.0.26100",
                    SizeBytes = 8L * gb
                }
            }
        };

        return new SimulationScenario(
            "custom-tiny11-older-pc",
            "Custom / Lite Windows 11 ISO on Older PC (Tiny11 + LabConfig Bypass)",
            "Custom/modified Windows 11 Lite ISO (root setup.exe & boot.sdi omitted, 2.75 GB ESD) on a 3 GB RAM / no-TPM SATA SSD PC — auto-enables LabConfig bypass & boot.sdi synthesis.",
            env,
            customIso);
    }
}
