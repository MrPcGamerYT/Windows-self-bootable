using System.Text.RegularExpressions;
using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Services;

public sealed partial class SmartCompatibilityAnalyzer
{
    [GeneratedRegex(@"10\.0\.(?<build>\d{4,5})", RegexOptions.CultureInvariant)]
    private static partial Regex BuildFromVersionRegex();

    public static StorageDriveKind ClassifyDriveKind(
        string busType,
        string mediaType,
        string controllerService,
        string friendlyName)
    {
        string bus = (busType ?? string.Empty).Trim().ToUpperInvariant();
        string media = (mediaType ?? string.Empty).Trim().ToUpperInvariant();
        string svc = (controllerService ?? string.Empty).Trim().ToUpperInvariant();
        string model = (friendlyName ?? string.Empty).Trim().ToUpperInvariant();

        // 1. Check RAID / Intel VMD / Intel RST / AMD RAID controllers first
        if (bus == "RAID" ||
            svc is "IASTORVD" or "IASTORAC" or "IASTORE" or "IASTORAVC" or "RCRAID" or "RCBOTTOM" or "AMDRAID")
        {
            return StorageDriveKind.RaidOrVmd;
        }

        // 2. Check Virtual / Hypervisor disks (Hyper-V, VMware, QEMU/VirtIO, VirtualBox, VHDX)
        if (bus is "VIRTUAL" or "FILEBACKEDVIRTUAL" ||
            svc is "VIOSCSI" or "VIOSTOR" or "PVSCSI" or "VMSCSI" ||
            model.Contains("VIRTUAL") || model.Contains("VMWARE") || model.Contains("QEMU") || model.Contains("VBOX"))
        {
            return StorageDriveKind.VirtualDisk;
        }

        // 3. Check Embedded Flash (eMMC / SD / UFS on compact laptops & tablets)
        if (bus is "MMC" or "SD" or "UFS" || model.Contains("EMMC") || model.Contains("UFS"))
        {
            return StorageDriveKind.EmmcOrUfs;
        }

        // 4. Check NVMe PCIe SSDs
        if (bus is "NVME" or "SCM" || svc == "STORNVME" || model.Contains("NVME"))
        {
            return StorageDriveKind.NvmeSsd;
        }

        // 5. Check Mechanical Hard Drives (HDD)
        if (media == "HDD" || model.Contains("HARD DISK") || model.Contains("BARRACUDA") || model.Contains("HDD"))
        {
            return StorageDriveKind.Hdd;
        }

        // 6. Check SATA / AHCI / SAS SSDs
        if (media == "SSD" || bus is "SATA" or "ATA" or "SAS" || model.Contains("SSD"))
        {
            return media == "HDD" ? StorageDriveKind.Hdd : StorageDriveKind.SataSsd;
        }

        return StorageDriveKind.Unknown;
    }

    public DriveOptimizationPlan BuildDriveOptimizationPlan(
        DiskSnapshot? targetDisk,
        ShrinkAnalysis? shrinkAnalysis)
    {
        if (targetDisk is null)
        {
            return new DriveOptimizationPlan
            {
                DriveKind = StorageDriveKind.Unknown,
                DriveSummary = "Unknown storage device",
                CopyBufferSizeBytes = 1024 * 1024,
                RecommendPreShrinkConsolidation = false,
                SupportsTrim = false,
                AutoExportDriversToWinPe = true,
                AutoInjectDriversIntoBootWim = false,
                DriverReserveBytes = 256L * ByteFormatting.OneMb,
                TuningExplanation = "Using safe default 1 MB buffered I/O and $WinPEDriver$ storage driver export."
            };
        }

        bool needsBootWimDriverInjection = targetDisk.RequiresWinPeDriverInjection ||
                                           targetDisk.DriveKind is StorageDriveKind.RaidOrVmd or StorageDriveKind.EmmcOrUfs;

        bool hasUnmovableTail = shrinkAnalysis is { UnmovableTailBytes: > ByteFormatting.OneGb };

        (int copyBuf, bool preShrinkConsolidate, bool supportsTrim, string note) = targetDisk.DriveKind switch
        {
            StorageDriveKind.Hdd => (
                4 * 1024 * 1024, // 4 MB large sequential buffer minimizes HDD head seeks
                hasUnmovableTail,
                false,
                "Mechanical HDD detected: tuned to 4 MB sequential write buffers (minimizes head thrashing), disabled TRIM, and enabled optional NTFS tail consolidation before shrink."),

            StorageDriveKind.NvmeSsd => (
                2 * 1024 * 1024, // 2 MB high-throughput NVMe buffer
                false,
                true,
                $"NVMe PCIe SSD detected ({targetDisk.SectorGeometryDisplay}): tuned for high-throughput async I/O and fast ReTrim (defrag disabled to prevent flash wear)."),

            StorageDriveKind.RaidOrVmd => (
                2 * 1024 * 1024,
                false,
                targetDisk.SupportsTrim,
                $"Intel VMD / RAID controller detected (driver '{targetDisk.ControllerServiceName}'): automatically injecting active storage drivers into boot.wim + $WinPEDriver$ so Windows Setup detects C: without 'Load driver' prompts!"),

            StorageDriveKind.EmmcOrUfs => (
                1024 * 1024,
                false,
                targetDisk.SupportsTrim,
                "Embedded eMMC/UFS storage detected: automatically exporting motherboard SDHCI/UFS & chipset drivers into $WinPEDriver$ + boot.wim so Setup sees internal flash storage."),

            StorageDriveKind.VirtualDisk => (
                2 * 1024 * 1024,
                false,
                targetDisk.SupportsTrim,
                needsBootWimDriverInjection
                    ? $"Virtual disk using custom SCSI controller ('{targetDisk.ControllerServiceName}'): auto-injecting paravirtual storage driver into boot.wim + $WinPEDriver$."
                    : "Virtual machine disk detected: using standard hypervisor storage pipeline."),

            _ => (
                1024 * 1024,
                false,
                true,
                $"SATA / AHCI SSD detected ({targetDisk.SectorGeometryDisplay}): tuned with 1 MB async write buffers and TRIM-safe partition handling.")
        };

        long driverReserve = needsBootWimDriverInjection
            ? 512L * ByteFormatting.OneMb
            : 256L * ByteFormatting.OneMb;

        string controllerLabel = string.IsNullOrWhiteSpace(targetDisk.ControllerServiceName)
            ? targetDisk.BusType
            : $"{targetDisk.BusType} / {targetDisk.ControllerServiceName}";

        return new DriveOptimizationPlan
        {
            DriveKind = targetDisk.DriveKind,
            DriveSummary = $"{targetDisk.DriveKindDisplay} — {targetDisk.FriendlyName} ({controllerLabel}, {targetDisk.SectorGeometryDisplay})",
            CopyBufferSizeBytes = copyBuf,
            RecommendPreShrinkConsolidation = preShrinkConsolidate,
            SupportsTrim = supportsTrim,
            AutoExportDriversToWinPe = true,
            AutoInjectDriversIntoBootWim = needsBootWimDriverInjection,
            DriverReserveBytes = driverReserve,
            TuningExplanation = note
        };
    }

    public SmartCompatibilityReport Analyze(
        SystemEnvironmentSnapshot env,
        IsoInspectionResult? iso)
    {
        DriveOptimizationPlan drivePlan = BuildDriveOptimizationPlan(env.TargetDisk, env.ShrinkInfo);
        var advisories = new List<SystemCheckItem>();

        advisories.Add(new SystemCheckItem
        {
            Id = "smart-drive-tuning",
            Category = "Smart Drive",
            Title = $"Drive Auto-Tuning: {env.TargetDisk?.DriveKindDisplay ?? "Internal Drive"}",
            Severity = env.TargetDisk?.RequiresWinPeDriverInjection == true ? CheckSeverity.Warning : CheckSeverity.Pass,
            Summary = drivePlan.TuningExplanation
        });

        AppendSmartPartitionStyleTopologyAdvisory(env, advisories);

        int currentBuild = env.CurrentWindows?.BuildNumber ?? 0;

        if (iso is null || !iso.IsValid)
        {
            return new SmartCompatibilityReport
            {
                TransitionKind = WindowsTransitionKind.Unknown,
                TargetWindowsGeneration = "Select ISO",
                CurrentBuildNumber = currentBuild,
                TargetBuildNumber = 0,
                TransitionBadge = "AWAITING ISO",
                TransitionHeadline = "Select any Official or Custom Windows 10 / 11 ISO to analyze Upgrade, Clean Install, or Downgrade",
                TransitionExplanation = drivePlan.TuningExplanation,
                SupportsInPlaceKeepAppsUpgrade = false,
                DrivePlan = drivePlan,
                RecommendedOptions = new SmartTailoringOptions
                {
                    ExportHostStorageAndNetworkDrivers = drivePlan.AutoExportDriversToWinPe,
                    InjectBootCriticalStorageDriversIntoBootWim = drivePlan.AutoInjectDriversIntoBootWim,
                    WriteEiCfgToUnlockAllEditions = true,
                    BypassWin11HardwareChecks = false,
                    RunDriveAwarePreShrinkOptimization = drivePlan.RecommendPreShrinkConsolidation
                },
                CompatibilityAdvisories = advisories
            };
        }

        int targetBuild = InferIsoBuildNumber(iso);
        string targetGen = InferWindowsGeneration(targetBuild, iso);

        WindowsTransitionKind transition = (currentBuild, targetBuild) switch
        {
            (> 0, > 0) when targetBuild > currentBuild => WindowsTransitionKind.UpgradeToNewerWindows,
            (> 0, > 0) when targetBuild < currentBuild => WindowsTransitionKind.DowngradeToOlderWindows,
            (> 0, > 0) => WindowsTransitionKind.CleanReinstallSameBuild,
            _ => WindowsTransitionKind.CleanReinstallSameBuild
        };

        (int? matchedIdx, string? matchedName) = MatchBestIsoEdition(env.CurrentWindows?.EditionId, iso.Editions);

        bool archMatches = string.Equals(iso.Architecture, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(iso.Architecture, env.HostArchitecture, StringComparison.OrdinalIgnoreCase);
        bool supportsInPlace = iso.HasSetupExe &&
                               archMatches &&
                               transition is WindowsTransitionKind.UpgradeToNewerWindows or WindowsTransitionKind.CleanReinstallSameBuild;

        // Evaluate target Windows generation hardware rules (Win11 vs Win10)
        bool isTargetWin11 = targetGen.Contains("11", StringComparison.OrdinalIgnoreCase) || targetBuild >= 22000;
        bool recommendWin11Bypass = EvaluateHardwareForTargetOs(env, iso, isTargetWin11, transition, advisories);

        (string badge, string headline, string explanation) = BuildTransitionMessaging(
            transition,
            env.CurrentWindows,
            targetGen,
            targetBuild,
            matchedName);

        if (!archMatches)
        {
            advisories.Add(new SystemCheckItem
            {
                Id = "smart-cross-bitness-drivers",
                Category = "Cross-Bitness",
                Title = $"Cross-Bitness Transition ({env.HostArchitecture} Host → {iso.Architecture} ISO)",
                Severity = CheckSeverity.Pass,
                Summary = $"Automatically skipping {env.HostArchitecture} host kernel driver injection so {iso.Architecture} Windows Setup boots cleanly with native {iso.Architecture} inbox storage drivers."
            });
        }

        var recommendedOptions = new SmartTailoringOptions
        {
            WorkflowChoice = InstallWorkflowChoice.SelfBootableCleanInstall,
            ExportHostStorageAndNetworkDrivers = archMatches,
            InjectBootCriticalStorageDriversIntoBootWim = archMatches && drivePlan.AutoInjectDriversIntoBootWim,
            WriteEiCfgToUnlockAllEditions = true,
            BypassWin11HardwareChecks = recommendWin11Bypass,
            RunDriveAwarePreShrinkOptimization = drivePlan.RecommendPreShrinkConsolidation
        };

        return new SmartCompatibilityReport
        {
            TransitionKind = transition,
            TargetWindowsGeneration = targetGen,
            CurrentBuildNumber = currentBuild,
            TargetBuildNumber = targetBuild,
            TransitionBadge = badge,
            TransitionHeadline = headline,
            TransitionExplanation = explanation,
            SupportsInPlaceKeepAppsUpgrade = supportsInPlace,
            MatchedEditionIndex = matchedIdx,
            MatchedEditionName = matchedName,
            DrivePlan = drivePlan,
            RecommendedOptions = recommendedOptions,
            CompatibilityAdvisories = advisories
        };
    }

    private static bool EvaluateHardwareForTargetOs(
        SystemEnvironmentSnapshot env,
        IsoInspectionResult iso,
        bool isTargetWin11,
        WindowsTransitionKind transition,
        List<SystemCheckItem> advisories)
    {
        PcHardwareCapabilities? hw = env.Hardware;
        DiskSnapshot? disk = env.TargetDisk;
        bool recommendWin11Bypass = false;

        if (iso.IsCustomOrModifiedIso)
        {
            advisories.Add(new SystemCheckItem
            {
                Id = "custom-iso-profile",
                Category = "ISO Type",
                Title = "Custom / Modified Windows ISO Detected",
                Severity = CheckSeverity.Pass,
                Summary = "Custom, Lite, AIO, or modified Windows ISO structure verified. All custom scripts, $OEM$ folders, and unattended settings inside the ISO will be preserved automatically."
            });
        }

        if (isTargetWin11)
        {
            if (hw is not null)
            {
                bool ramOk = hw.TotalRamBytes <= 0 || hw.TotalRamBytes >= (4L * ByteFormatting.OneGb) - (256L * ByteFormatting.OneMb);
                if (!ramOk)
                {
                    recommendWin11Bypass = true;
                }

                advisories.Add(new SystemCheckItem
                {
                    Id = "win11-ram",
                    Category = "Win11 Compat",
                    Title = $"System Memory ({hw.RamDisplay})",
                    Severity = ramOk ? CheckSeverity.Pass : CheckSeverity.Warning,
                    Summary = ramOk
                        ? $"{hw.RamDisplay} installed (meets Windows 11 4 GB baseline)."
                        : $"{hw.RamDisplay} detected (below stock Windows 11 4 GB check; SWBI auto-enables LabConfig RAM/TPM bypass so Official & Lite/Tiny11 ISOs install cleanly)."
                });

                if (!hw.HasTpm20 || env.SecureBootEnabled == false)
                {
                    recommendWin11Bypass = true;
                }

                advisories.Add(new SystemCheckItem
                {
                    Id = "win11-tpm",
                    Category = "Win11 Compat",
                    Title = hw.HasTpm20 ? "TPM 2.0 Security Module Ready" : $"TPM Check ({(hw.TpmPresent ? hw.TpmVersion : "Not Detected")} — Auto-Bypass Ready)",
                    Severity = hw.HasTpm20 ? CheckSeverity.Pass : CheckSeverity.Warning,
                    Summary = hw.HasTpm20
                        ? "TPM 2.0 is active and ready for Windows 11 Setup."
                        : "TPM 2.0 was not reported. SWBI automatically enables Win11 Hardware Check Bypass (LabConfig) in Step 2 so both Official and Custom Windows 11 ISOs install without 'This PC can't run Windows 11' blocks.",
                    RemediationHint = hw.HasTpm20 ? null : "Keep 'Bypass Windows 11 TPM/CPU/RAM Checks' enabled in Step 2, or enable Intel PTT / AMD fTPM in BIOS."
                });
            }

            if (disk is not null && disk.SizeBytes < 60L * ByteFormatting.OneGb)
            {
                recommendWin11Bypass = true;
                advisories.Add(new SystemCheckItem
                {
                    Id = "win11-disk-min",
                    Category = "Win11 Compat",
                    Title = $"Compact Drive Capacity ({disk.SizeDisplay})",
                    Severity = CheckSeverity.Warning,
                    Summary = "Drive is under 64 GB; LabConfig storage-check bypass is auto-enabled for Lite/Compact Windows 11 installs."
                });
            }

            if (env.Firmware == FirmwareMode.LegacyBios || disk?.PartitionStyle == PartitionStyleType.Mbr)
            {
                recommendWin11Bypass = true;
                advisories.Add(new SystemCheckItem
                {
                    Id = "win11-mbr-bios",
                    Category = "MBR / BIOS",
                    Title = "Smart MBR / Legacy BIOS Windows 11 Mode Active",
                    Severity = CheckSeverity.Warning,
                    Summary = "Legacy BIOS / MBR detected for Windows 11: SWBI automatically enables LabConfig (BypassSecureBootCheck + BypassTPMCheck) and arms Legacy BIOS WinPE (winload.exe) so Windows 11 installs cleanly onto MBR without requiring UEFI conversion!"
                });
            }
        }
        else
        {
            // Target is Windows 10 (including Downgrade / Degrade from Windows 11 -> Windows 10!)
            if (hw is not null)
            {
                bool ramOk = hw.TotalRamBytes <= 0 || hw.TotalRamBytes >= 2L * ByteFormatting.OneGb;
                advisories.Add(new SystemCheckItem
                {
                    Id = "win10-ram",
                    Category = "Win10 Compat",
                    Title = $"Windows 10 Hardware Fit ({hw.RamDisplay} RAM)",
                    Severity = ramOk ? CheckSeverity.Pass : CheckSeverity.Warning,
                    Summary = ramOk
                        ? $"{hw.CpuName} with {hw.RamDisplay} RAM easily meets Windows 10 requirements (TPM 2.0 not required)."
                        : $"Low system RAM ({hw.RamDisplay}); Lite/32-bit or compact Windows 10 editions recommended."
                });
            }

            if (transition == WindowsTransitionKind.DowngradeToOlderWindows)
            {
                advisories.Add(new SystemCheckItem
                {
                    Id = "downgrade-guidance",
                    Category = "Downgrade",
                    Title = "Smart Downgrade ('Degrade') Mode Active",
                    Severity = CheckSeverity.Info,
                    Summary = env.CurrentWindows?.CanRollbackToPreviousOs == true
                        ? $"Note: Windows still has an active Windows.old rollback window in Settings > System > Recovery, OR you can proceed with a clean self-bootable downgrade via '{SwbiBrand.PartitionVolumeLabel}' ({SwbiBrand.ShortName}). Storage & Wi-Fi drivers will be exported automatically."
                        : $"Microsoft blocks keeping apps during an older-build downgrade, so booting into our dedicated '{SwbiBrand.PartitionVolumeLabel}' ({SwbiBrand.BrandedTitle}) partition is the ideal USB-free way to downgrade cleanly while exporting your PC's storage & network drivers."
                });
            }
        }

        return recommendWin11Bypass;
    }

    private static void AppendSmartPartitionStyleTopologyAdvisory(
        SystemEnvironmentSnapshot env,
        List<SystemCheckItem> advisories)
    {
        DiskSnapshot? disk = env.TargetDisk;
        if (disk is null)
        {
            return;
        }

        if (disk.PartitionStyle == PartitionStyleType.Gpt && env.Firmware == FirmwareMode.Uefi)
        {
            advisories.Add(new SystemCheckItem
            {
                Id = "smart-partition-topology",
                Category = "MBR / GPT",
                Title = "Smart Partition Topology: Modern GPT + UEFI",
                Severity = CheckSeverity.Pass,
                Summary = $"Disk {disk.DiskNumber} uses GPT with UEFI firmware ({disk.Partitions.Count} partition(s), >2 TB capable, up to 128 primary partitions) — SWBI will create a GPT Basic Data partition and arm UEFI + BCD boot."
            });
            return;
        }

        if (disk.PartitionStyle == PartitionStyleType.Mbr && env.Firmware == FirmwareMode.LegacyBios)
        {
            int usedSlots = disk.EffectiveMbrPrimarySlotsUsed;
            bool hasSeparateSystem = disk.Partitions.Any(p =>
                p.PartitionNumber != env.TargetOsPartitionNumber &&
                (p.IsSystem || p.IsActiveLegacy || p.Role == PartitionRole.SystemReservedLegacy));
            string extNote = disk.HasMbrExtendedPartition
                ? " Extended/Logical EBR partition chain detected and will be preserved byte-for-byte."
                : string.Empty;
            string ceilingNote = disk.ExceedsMbr2TibLimit
                ? " Disk exceeds MBR's 2 TiB LBA32 limit; SWBI safely places SWBI-SETUP inside the addressable 2 TiB zone."
                : string.Empty;

            if (usedSlots < 4)
            {
                string modeDesc = hasSeparateSystem
                    ? $"Dedicated active System Reserved boot partition detected ({usedSlots}/4 primary MBR slots used)."
                    : $"Single-partition Active C: MBR layout detected ({usedSlots}/4 primary MBR slots used) — SWBI preserves C:'s Active (0x80) boot flag and tailors your Setup Guide to Format C: in place.";

                advisories.Add(new SystemCheckItem
                {
                    Id = "smart-partition-topology",
                    Category = "MBR / GPT",
                    Title = $"Smart Partition Topology: Legacy BIOS + MBR ({usedSlots}/4 Primary Slots)",
                    Severity = disk.ExceedsMbr2TibLimit ? CheckSeverity.Warning : CheckSeverity.Pass,
                    Summary = $"{modeDesc} SWBI will create an MBR FAT32 LBA (0x0C) primary slot and arm Legacy BIOS winload.exe boot.{extNote}{ceilingNote}"
                });
            }
        }
    }

    private static (string Badge, string Headline, string Explanation) BuildTransitionMessaging(
        WindowsTransitionKind kind,
        CurrentWindowsInfo? currentOs,
        string targetGen,
        int targetBuild,
        string? matchedEdition)
    {
        string curDesc = currentOs?.SummaryDisplay ?? "Current Windows";
        string tgtDesc = targetBuild > 0 ? $"{targetGen} (Build {targetBuild})" : targetGen;
        string editionNote = matchedEdition is not null
            ? $" Auto-matched edition for your PC's digital license: '{matchedEdition}'."
            : " Writes sources\\ei.cfg so all editions inside the ISO are selectable during Setup.";

        return kind switch
        {
            WindowsTransitionKind.UpgradeToNewerWindows => (
                "SMART UPGRADE",
                $"Upgrade Detected: {curDesc}  →  {tgtDesc}",
                $"Your ISO is newer than your running OS. You can boot into '{SwbiBrand.PartitionVolumeLabel}' ({SwbiBrand.ShortName}) for a 100% clean upgrade OR run an In-Place Upgrade keeping apps & files.{editionNote}"),

            WindowsTransitionKind.DowngradeToOlderWindows => (
                "SMART DOWNGRADE",
                $"Downgrade ('Degrade') Detected: {curDesc}  →  {tgtDesc}",
                $"Your ISO is an earlier Windows build/release. Because Windows Setup disables 'Keep apps' when moving to an older build, booting into '{SwbiBrand.PartitionVolumeLabel}' ({SwbiBrand.BrandedTitle}) lets you cleanly install {tgtDesc} without a USB pendrive while preserving exported storage & network drivers.{editionNote}"),

            _ => (
                "CLEAN REINSTALL",
                $"Same-Build Reinstall: {curDesc}  →  {tgtDesc}",
                $"Fresh self-bootable installation of {tgtDesc} via '{SwbiBrand.PartitionVolumeLabel}' tailored to your drive and storage controller.{editionNote}")
        };
    }

    public static int InferIsoBuildNumber(IsoInspectionResult iso)
    {
        foreach (WimEditionInfo ed in iso.Editions)
        {
            if (!string.IsNullOrWhiteSpace(ed.Version))
            {
                Match m = BuildFromVersionRegex().Match(ed.Version);
                if (m.Success && int.TryParse(m.Groups["build"].Value, out int parsed))
                {
                    return parsed;
                }
            }
        }

        string haystack = string.Join(" ", iso.Editions.Select(e => $"{e.Name} {e.Description}")) + " " + Path.GetFileName(iso.IsoPath);

        if (haystack.Contains("25H2", StringComparison.OrdinalIgnoreCase)) return 26200;
        if (haystack.Contains("24H2", StringComparison.OrdinalIgnoreCase)) return 26100;
        if (haystack.Contains("23H2", StringComparison.OrdinalIgnoreCase)) return 22631;

        bool mentions11 = haystack.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) ||
                          haystack.Contains("Win11", StringComparison.OrdinalIgnoreCase) ||
                          haystack.Contains("Win 11", StringComparison.OrdinalIgnoreCase) ||
                          haystack.Contains("W11", StringComparison.OrdinalIgnoreCase) ||
                          haystack.Contains("Tiny11", StringComparison.OrdinalIgnoreCase);

        bool mentions10 = haystack.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ||
                          haystack.Contains("Win10", StringComparison.OrdinalIgnoreCase) ||
                          haystack.Contains("Win 10", StringComparison.OrdinalIgnoreCase) ||
                          haystack.Contains("W10", StringComparison.OrdinalIgnoreCase) ||
                          haystack.Contains("Tiny10", StringComparison.OrdinalIgnoreCase);

        if (mentions11) return 26100;
        if (mentions10) return 19045;
        return 0;
    }

    public static string InferWindowsGeneration(int buildNumber, IsoInspectionResult iso)
    {
        string haystack = string.Join(" ", iso.Editions.Select(e => $"{e.Name} {e.Description}")) + " " + Path.GetFileName(iso.IsoPath);

        if (buildNumber >= 22000 ||
            haystack.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("Win11", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("Win 11", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("W11", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("Tiny11", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows 11";
        }

        if (buildNumber is >= 10240 and < 22000 ||
            haystack.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("Win10", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("Win 10", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("W10", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("Tiny10", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows 10";
        }

        return "Windows";
    }

    public static (int? Index, string? Name) MatchBestIsoEdition(
        string? currentEditionId,
        IReadOnlyList<WimEditionInfo> editions)
    {
        if (editions.Count == 0 || string.IsNullOrWhiteSpace(currentEditionId))
        {
            return (null, null);
        }

        string needle = currentEditionId.Trim() switch
        {
            var s when s.Equals("CoreSingleLanguage", StringComparison.OrdinalIgnoreCase) => "Home Single Language",
            var s when s.Equals("Core", StringComparison.OrdinalIgnoreCase) => "Home",
            var s when s.Equals("ProfessionalWorkstation", StringComparison.OrdinalIgnoreCase) => "Pro for Workstations",
            var s when s.Equals("ProfessionalEducation", StringComparison.OrdinalIgnoreCase) => "Pro Education",
            var s when s.Equals("Professional", StringComparison.OrdinalIgnoreCase) => "Pro",
            var s when s.Equals("Education", StringComparison.OrdinalIgnoreCase) => "Education",
            var s when s.Equals("Enterprise", StringComparison.OrdinalIgnoreCase) => "Enterprise",
            var other => other
        };

        // Prefer exact suffix match (e.g. "Windows 11 Pro" rather than "Windows 11 Pro Education")
        WimEditionInfo? exact = editions.FirstOrDefault(e =>
            e.Name.EndsWith($" {needle}", StringComparison.OrdinalIgnoreCase) ||
            e.Name.Equals(needle, StringComparison.OrdinalIgnoreCase));

        WimEditionInfo? match = exact ?? editions.FirstOrDefault(e =>
            e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));

        return match is not null ? (match.Index, match.Name) : (null, null);
    }
}
