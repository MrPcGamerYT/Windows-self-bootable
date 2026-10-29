using Microsoft.Win32;
using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Services;

public sealed class ReadinessEvaluator
{
    private readonly PartitionSizingCalculator _sizingCalculator;
    private readonly SmartCompatibilityAnalyzer _compatibilityAnalyzer;

    public ReadinessEvaluator(
        PartitionSizingCalculator sizingCalculator,
        SmartCompatibilityAnalyzer? compatibilityAnalyzer = null)
    {
        _sizingCalculator = sizingCalculator;
        _compatibilityAnalyzer = compatibilityAnalyzer ?? new SmartCompatibilityAnalyzer();
    }

    public ReadinessReport Evaluate(
        SystemEnvironmentSnapshot env,
        IsoInspectionResult? iso)
    {
        var checks = new List<SystemCheckItem>();
        bool requiresBitLockerConfirmation = false;

        SmartCompatibilityReport compatibility = _compatibilityAnalyzer.Analyze(env, iso);
        bool isTargetWin11 = iso is not null &&
                             (compatibility.TargetWindowsGeneration.Contains("11", StringComparison.OrdinalIgnoreCase) ||
                              compatibility.TargetBuildNumber >= 22000);

        // 1. Administrator elevation check
        checks.Add(env.IsElevated
            ? new SystemCheckItem
            {
                Id = "admin-elevation",
                Category = "Permissions",
                Title = "Administrator Privileges",
                Severity = CheckSeverity.Pass,
                Summary = "Running elevated with Administrator privileges."
            }
            : new SystemCheckItem
            {
                Id = "admin-elevation",
                Category = "Permissions",
                Title = "Administrator Privileges Required",
                Severity = CheckSeverity.Blocker,
                Summary = "Process is not elevated. Live disk inspection, shrinking, DISM, driver export, and BCD changes require Administrator rights.",
                RemediationHint = "Restart the application as Administrator (Dry-Run simulation remains available)."
            });

        // 2. Smart Firmware + Partition Style Pairing (UEFI+GPT native, Legacy BIOS+MBR smart support for Win10 & Win11, mismatch protection)
        DiskSnapshot? targetDisk = env.TargetDisk;
        int mbrUsedPrimarySlots = targetDisk?.EffectiveMbrPrimarySlotsUsed ?? 0;
        bool hasSeparateLegacySystemPartition = targetDisk?.Partitions.Any(p =>
            p.PartitionNumber != env.TargetOsPartitionNumber &&
            (p.IsSystem || p.IsActiveLegacy || p.Role == PartitionRole.SystemReservedLegacy)) == true;

        bool legacyMbrEligible = env.Firmware == FirmwareMode.LegacyBios &&
                                 targetDisk?.PartitionStyle == PartitionStyleType.Mbr &&
                                 mbrUsedPrimarySlots < 4;

        bool legacyOnGptMismatch = env.Firmware == FirmwareMode.LegacyBios &&
                                   targetDisk?.PartitionStyle == PartitionStyleType.Gpt;

        bool uefiOnMbrMismatch = env.Firmware == FirmwareMode.Uefi &&
                                 targetDisk?.PartitionStyle == PartitionStyleType.Mbr;

        checks.Add(env.Firmware switch
        {
            FirmwareMode.Uefi when !uefiOnMbrMismatch => new SystemCheckItem
            {
                Id = "firmware-mode",
                Category = "Firmware",
                Title = "UEFI Firmware Mode",
                Severity = CheckSeverity.Pass,
                Summary = env.SecureBootEnabled == true
                    ? "System is booted in UEFI mode with Secure Boot enabled (fully compatible with Windows 10 & Windows 11 on GPT)."
                    : "System is booted in UEFI mode (compatible with Windows 10 & Windows 11 on GPT)."
            },
            FirmwareMode.Uefi => new SystemCheckItem
            {
                Id = "firmware-mode",
                Category = "Firmware",
                Title = "UEFI Firmware Detected on MBR Disk (Mismatch Warning)",
                Severity = CheckSeverity.Warning,
                Summary = "Motherboard is in UEFI mode while Disk is formatted as MBR. Clean UEFI Setup requires GPT (see Disk check below for lossless mbr2gpt / CSM options)."
            },
            FirmwareMode.LegacyBios when legacyMbrEligible => new SystemCheckItem
            {
                Id = "firmware-mode",
                Category = "Firmware",
                Title = isTargetWin11
                    ? "Legacy BIOS + MBR Smart Mode (Windows 11 Auto-Bypass Active)"
                    : "Legacy BIOS + MBR Smart Mode Ready",
                Severity = CheckSeverity.Warning,
                Summary = isTargetWin11
                    ? $"Legacy BIOS + MBR ({mbrUsedPrimarySlots}/4 primary slots used): SWBI auto-enables LabConfig SecureBoot/TPM bypass and arms winload.exe so Windows 11 installs cleanly on MBR."
                    : hasSeparateLegacySystemPartition
                        ? $"Legacy BIOS detected with dedicated active System Reserved partition ({mbrUsedPrimarySlots}/4 primary MBR slots used). Will arm winload.exe BCD boot."
                        : $"Single-partition Active C: Legacy BIOS MBR layout ({mbrUsedPrimarySlots}/4 primary MBR slots used). Will arm winload.exe BCD boot and guide in-place C: Format during Setup."
            },
            FirmwareMode.LegacyBios when legacyOnGptMismatch => new SystemCheckItem
            {
                Id = "firmware-mode",
                Category = "Firmware",
                Title = "Firmware / Disk Mismatch: Legacy BIOS Booted on GPT Disk",
                Severity = CheckSeverity.Blocker,
                Summary = "System booted in Legacy BIOS (CSM) mode, while the target disk uses GPT. Windows Setup in Legacy BIOS mode refuses GPT disks ('The selected disk is of the GPT partition style').",
                RemediationHint = "Reboot into BIOS/UEFI settings and switch Boot Mode from Legacy/CSM to native UEFI."
            },
            FirmwareMode.LegacyBios => new SystemCheckItem
            {
                Id = "firmware-mode",
                Category = "Firmware",
                Title = "Legacy BIOS MBR Table Full (4/4 Primary Slots Used)",
                Severity = CheckSeverity.Blocker,
                Summary = $"Legacy MBR Disk {targetDisk?.DiskNumber ?? 0} already occupies all 4 primary MBR partition table slots ({mbrUsedPrimarySlots}/4), leaving no primary slot for '{SwbiBrand.PartitionVolumeLabel}'.",
                RemediationHint = "Convert the disk losslessly to GPT ('mbr2gpt /convert /allowFullOS' + enable UEFI in BIOS) or remove an unused non-boot primary partition."
            },
            _ => new SystemCheckItem
            {
                Id = "firmware-mode",
                Category = "Firmware",
                Title = "Unknown Firmware Mode",
                Severity = CheckSeverity.Blocker,
                Summary = "Could not verify UEFI or BIOS firmware boot state.",
                RemediationHint = "Run as Administrator on a Windows 10/11 system."
            }
        });

        // 3. Target Disk, Drive Type (HDD/SSD/NVMe/VMD/eMMC), Partition Table (GPT / MBR) & System Partition check
        if (targetDisk is null)
        {
            checks.Add(new SystemCheckItem
            {
                Id = "disk-layout",
                Category = "Disk",
                Title = "System Disk Not Found",
                Severity = CheckSeverity.Blocker,
                Summary = $"Unable to locate target disk #{env.TargetDiskNumber} hosting {env.SystemDriveLetter}:."
            });
        }
        else
        {
            if (targetDisk.PartitionStyle == PartitionStyleType.Gpt && !legacyOnGptMismatch)
            {
                checks.Add(new SystemCheckItem
                {
                    Id = "disk-gpt",
                    Category = "Disk",
                    Title = $"GPT Partition Table — {targetDisk.DriveKindDisplay} (Disk {targetDisk.DiskNumber})",
                    Severity = CheckSeverity.Pass,
                    Summary = $"Disk {targetDisk.DiskNumber} ({targetDisk.FriendlyName}, {targetDisk.SizeDisplay}, {targetDisk.SectorGeometryDisplay}) uses GPT."
                });

                bool hasEsp = targetDisk.Partitions.Any(p =>
                    p.Role == PartitionRole.EfiSystem ||
                    string.Equals(p.GptTypeGuid, GptPartitionTypeGuids.EfiSystem, StringComparison.OrdinalIgnoreCase));

                checks.Add(hasEsp
                    ? new SystemCheckItem
                    {
                        Id = "efi-system-partition",
                        Category = "Boot",
                        Title = "EFI System Partition Present",
                        Severity = CheckSeverity.Pass,
                        Summary = $"Found EFI System Partition on Disk {targetDisk.DiskNumber} to host the one-time Windows Setup boot entry."
                    }
                    : new SystemCheckItem
                    {
                        Id = "efi-system-partition",
                        Category = "Boot",
                        Title = "Missing EFI System Partition on Target Disk",
                        Severity = CheckSeverity.Blocker,
                        Summary = $"No EFI System Partition was detected on Disk {targetDisk.DiskNumber}.",
                        RemediationHint = "Ensure the EFI System Partition resides on the primary internal disk."
                    });
            }
            else if (legacyMbrEligible)
            {
                string extDetail = targetDisk.HasMbrExtendedPartition
                    ? " (Extended/Logical EBR partitions detected & preserved)"
                    : string.Empty;
                string bootDetail = hasSeparateLegacySystemPartition
                    ? "dedicated active System Reserved partition"
                    : "single-partition Active C: boot layout (Format C: in Setup — do not Delete)";

                checks.Add(new SystemCheckItem
                {
                    Id = "disk-gpt",
                    Category = "Disk",
                    Title = $"MBR Partition Table — {targetDisk.DriveKindDisplay} ({mbrUsedPrimarySlots}/4 Primary Slots Used)",
                    Severity = CheckSeverity.Warning,
                    Summary = $"Disk {targetDisk.DiskNumber}{extDetail} uses MBR with {bootDetail}. SWBI will allocate primary MBR slot #{mbrUsedPrimarySlots + 1} (FAT32 LBA 0x0C)."
                });
            }
            else if (uefiOnMbrMismatch)
            {
                string inPlaceHint = compatibility.SupportsInPlaceKeepAppsUpgrade
                    ? " (Tip: In-Place Upgrade via setup.exe works without rebooting, OR convert to GPT with 'mbr2gpt /convert /allowFullOS' for a clean bootable install.)"
                    : " Convert losslessly to GPT via 'mbr2gpt /convert /allowFullOS', or switch motherboard firmware to Legacy/CSM mode so Setup runs in MBR mode.";

                checks.Add(new SystemCheckItem
                {
                    Id = "disk-gpt",
                    Category = "Disk",
                    Title = $"UEFI + MBR Mismatch on Disk {targetDisk.DiskNumber}",
                    Severity = CheckSeverity.Blocker,
                    Summary = $"Disk {targetDisk.DiskNumber} uses an MBR partition table while Windows is booted in UEFI mode. UEFI Windows Setup refuses clean installs onto MBR disks ('On EFI systems, Windows can only be installed to GPT disks').",
                    RemediationHint = inPlaceHint.Trim()
                });
            }
            else if (legacyOnGptMismatch)
            {
                checks.Add(new SystemCheckItem
                {
                    Id = "disk-gpt",
                    Category = "Disk",
                    Title = $"Legacy BIOS + GPT Mismatch on Disk {targetDisk.DiskNumber}",
                    Severity = CheckSeverity.Blocker,
                    Summary = $"Disk {targetDisk.DiskNumber} uses GPT while the PC is booted in Legacy BIOS mode.",
                    RemediationHint = "Enable native UEFI boot mode in your motherboard firmware settings."
                });
            }
            else
            {
                checks.Add(new SystemCheckItem
                {
                    Id = "disk-gpt",
                    Category = "Disk",
                    Title = targetDisk.PartitionStyle == PartitionStyleType.Mbr
                        ? $"MBR Partition Table Full ({mbrUsedPrimarySlots}/4 Primary Slots Used)"
                        : $"Unsupported Partition Style ({targetDisk.PartitionStyle})",
                    Severity = CheckSeverity.Blocker,
                    Summary = targetDisk.PartitionStyle == PartitionStyleType.Mbr
                        ? $"Disk {targetDisk.DiskNumber} already uses {mbrUsedPrimarySlots}/4 primary MBR slots; MBR allows at most 4 primary/extended slots."
                        : $"Disk {targetDisk.DiskNumber} uses {targetDisk.PartitionStyle}; GPT or MBR (< 4 primary slots) is required.",
                    RemediationHint = "Convert the disk safely without data loss using 'mbr2gpt /convert /allowFullOS' and enable UEFI, or free 1 primary MBR slot."
                });
            }

            // Check Disk Health (SMART / Storage Operational Status across HDD, SATA SSD, NVMe, RAID, eMMC)
            checks.Add(targetDisk.Health switch
            {
                DiskHealthState.Healthy => new SystemCheckItem
                {
                    Id = "disk-health",
                    Category = "Hardware",
                    Title = $"Drive Health: {targetDisk.DriveKindDisplay}",
                    Severity = CheckSeverity.Pass,
                    Summary = $"Disk {targetDisk.DiskNumber} ({targetDisk.FriendlyName}) reports Healthy ({targetDisk.OperationalStatus})."
                },
                DiskHealthState.Warning => new SystemCheckItem
                {
                    Id = "disk-health",
                    Category = "Hardware",
                    Title = "Drive Health Warning Reported",
                    Severity = CheckSeverity.Warning,
                    Summary = $"Disk {targetDisk.DiskNumber} reports Warning ({targetDisk.OperationalStatus}). Shrinking a degraded drive carries elevated risk.",
                    RemediationHint = "Verify full external backups and check SMART diagnostics before proceeding."
                },
                DiskHealthState.Unhealthy => new SystemCheckItem
                {
                    Id = "disk-health",
                    Category = "Hardware",
                    Title = "Critical Drive Health Failure",
                    Severity = CheckSeverity.Blocker,
                    Summary = $"Disk {targetDisk.DiskNumber} reports Unhealthy ({targetDisk.OperationalStatus}). Stopping to prevent data loss.",
                    RemediationHint = "Back up data immediately and replace the failing drive instead of resizing partitions."
                },
                _ => new SystemCheckItem
                {
                    Id = "disk-health",
                    Category = "Hardware",
                    Title = "Drive Health Status Unknown",
                    Severity = CheckSeverity.Info,
                    Summary = "Storage controller did not report SMART health state; ensure you have verified backups."
                }
            });

            // Check for pre-existing temporary installer partition
            PartitionSnapshot? existingInstaller = targetDisk.Partitions.FirstOrDefault(p =>
                p.Role == PartitionRole.TemporaryInstaller ||
                string.Equals(p.Label, InstallerManifest.DefaultVolumeLabel, StringComparison.OrdinalIgnoreCase));

            if (existingInstaller is not null)
            {
                checks.Add(new SystemCheckItem
                {
                    Id = "existing-installer",
                    Category = "Disk",
                    Title = "Existing Temporary Installer Partition Detected",
                    Severity = CheckSeverity.Blocker,
                    Summary = $"Disk {targetDisk.DiskNumber} Partition {existingInstaller.PartitionNumber} ({existingInstaller.SizeDisplay}) is already labeled '{existingInstaller.Label}'.",
                    RemediationHint = "Open '2. Post-Install Cleanup' to inspect and remove the previous temporary installer partition before preparing a new one."
                });
            }
        }

        // 4. Add Smart Drive & Target Windows Compatibility Advisories (HDD/SSD/NVMe/VMD + Upgrade/Downgrade)
        foreach (SystemCheckItem advisory in compatibility.CompatibilityAdvisories)
        {
            checks.Add(advisory);
        }

        // 5. Multi-disk topology awareness
        if (env.AllDisks.Count > 1)
        {
            checks.Add(new SystemCheckItem
            {
                Id = "multi-disk-notice",
                Category = "Disk",
                Title = $"Multiple Storage Devices Detected ({env.AllDisks.Count} drives)",
                Severity = CheckSeverity.Warning,
                Summary = $"Target C: drive is on Disk {env.TargetDiskNumber} ({targetDisk?.DriveKindDisplay}), while {env.AllDisks.Count - 1} other drive(s) are connected.",
                RemediationHint = "Disconnect non-essential USB drives before starting, and match partitions by Disk #, Size, and Label in Windows Setup."
            });
        }
        else
        {
            checks.Add(new SystemCheckItem
            {
                Id = "single-disk-topology",
                Category = "Disk",
                Title = "Single Physical Drive Layout",
                Severity = CheckSeverity.Pass,
                Summary = $"Single internal drive detected (Disk {env.TargetDiskNumber}, {targetDisk?.DriveKindDisplay ?? "Internal Drive"}) — ready for USB-free installation."
            });
        }

        // 6. BitLocker / Device Encryption check
        if (env.BitLocker is not null)
        {
            if (env.BitLocker.IsConversionInFlight)
            {
                checks.Add(new SystemCheckItem
                {
                    Id = "bitlocker-status",
                    Category = "Encryption",
                    Title = "BitLocker Encryption/Decryption In Progress",
                    Severity = CheckSeverity.Blocker,
                    Summary = $"Volume {env.BitLocker.VolumeLetter}: is currently {env.BitLocker.ConversionState}. Partition resizing is unsafe during active conversion.",
                    RemediationHint = "Wait for BitLocker encryption or decryption to reach 100% completion, then re-scan."
                });
            }
            else if (env.BitLocker.IsEncryptedOrActive)
            {
                requiresBitLockerConfirmation = true;
                string protectorNote = env.BitLocker.HasRecoveryPasswordProtector
                    ? $"Protectors detected: {env.BitLocker.KeyProtectorSummary}."
                    : "WARNING: No 48-digit RecoveryPassword protector was detected on this encrypted volume!";

                checks.Add(new SystemCheckItem
                {
                    Id = "bitlocker-status",
                    Category = "Encryption",
                    Title = "BitLocker / Device Encryption Active",
                    Severity = CheckSeverity.Warning,
                    Summary = $"Volume {env.BitLocker.VolumeLetter}: is encrypted ({env.BitLocker.ConversionState}). Partition and boot-path changes can trigger a BitLocker Recovery Key prompt if you return to the old OS. {protectorNote}",
                    RemediationHint = "Save your 48-digit BitLocker Recovery Key externally (e.g. aka.ms/myrecoverykey) and confirm below before proceeding."
                });
            }
            else
            {
                checks.Add(new SystemCheckItem
                {
                    Id = "bitlocker-status",
                    Category = "Encryption",
                    Title = "BitLocker / Device Encryption",
                    Severity = CheckSeverity.Pass,
                    Summary = $"Volume {env.BitLocker.VolumeLetter}: is not encrypted (FullyDecrypted)."
                });
            }
        }

        // 7. Power / Battery check
        if (env.PowerStatus is not null)
        {
            if (env.PowerStatus.HasBattery && !env.PowerStatus.IsOnAcPower)
            {
                int pct = env.PowerStatus.BatteryChargePercent ?? 50;
                bool criticalBattery = pct < 30;
                checks.Add(new SystemCheckItem
                {
                    Id = "power-ac",
                    Category = "Power",
                    Title = criticalBattery ? $"Critical Battery Level ({pct}%)" : $"Running on Battery Power ({pct}%)",
                    Severity = criticalBattery ? CheckSeverity.Blocker : CheckSeverity.Warning,
                    Summary = "Power loss during partition shrinking, DISM WIM splitting, or BCD updates can leave the system unbootable.",
                    RemediationHint = "Connect your laptop AC power adapter before starting preparation."
                });
            }
            else
            {
                checks.Add(new SystemCheckItem
                {
                    Id = "power-ac",
                    Category = "Power",
                    Title = "Power Source",
                    Severity = CheckSeverity.Pass,
                    Summary = env.PowerStatus.HasBattery
                        ? "AC power connected."
                        : "Desktop AC power supply active."
                });
            }
        }

        // 7b. Pending restart / servicing operation
        if (OperatingSystem.IsWindows())
        {
            (bool pendingRestart, string pendingReason) = DetectPendingRestart();
            if (pendingRestart)
            {
                checks.Add(new SystemCheckItem
                {
                    Id = "pending-reboot",
                    Category = "System",
                    Title = "Windows Has a Pending Restart",
                    Severity = CheckSeverity.Warning,
                    Summary =
                        $"Windows is waiting to finish a servicing operation ({pendingReason}). Until it restarts, files stay locked and " +
                        "unmovable, so C: may refuse to shrink; firmware and BCD writes are also less reliable during servicing.",
                    RemediationHint = "Restart Windows, let the update finish completely, then re-scan with SWBI before preparing."
                });
            }
            else
            {
                checks.Add(new SystemCheckItem
                {
                    Id = "pending-reboot",
                    Category = "System",
                    Title = "No Pending Restart",
                    Severity = CheckSeverity.Pass,
                    Summary = "No Windows Update or driver servicing operation is waiting for a restart."
                });
            }
        }

        // 8. ISO & Partition Sizing evaluation (accounting for $WinPEDriver$ & boot.wim driver reserve)
        PartitionSizingPlan? sizingPlan = null;
        if (iso is null)
        {
            checks.Add(new SystemCheckItem
            {
                Id = "iso-selected",
                Category = "ISO",
                Title = "Windows ISO Selection Pending",
                Severity = CheckSeverity.Info,
                Summary = "Select any Official or Custom/Modified Windows 10 or Windows 11 ISO (Official, UUPdump, Tiny10/11, AtlasOS, ReviOS, Ghost Spectre, NTLite, AIO) in Step 2."
            });
        }
        else if (!iso.IsValid)
        {
            string details = iso.ValidationErrors.Count > 0
                ? string.Join(" ", iso.ValidationErrors)
                : "Selected ISO does not contain a bootable WinPE (boot.wim) and Windows OS image (install.wim / install.esd / install.swm).";
            checks.Add(new SystemCheckItem
            {
                Id = "iso-validation",
                Category = "ISO",
                Title = "Invalid or Corrupted ISO Image",
                Severity = CheckSeverity.Blocker,
                Summary = details,
                RemediationHint = "Ensure the .iso file finished downloading completely and contains a Windows 10/11 installation image (sources\\boot.wim + install.wim/.esd/.swm)."
            });
        }
        else
        {
            bool exactArchMatch =
                string.Equals(iso.Architecture, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(iso.Architecture, env.HostArchitecture, StringComparison.OrdinalIgnoreCase);

            // Cross-bitness bootable clean install rules (32-bit <-> 64-bit on x86/x64 hardware):
            //  - 32-bit OS ('x86') -> 64-bit ISO ('x64'): Supported via bootable SWBI-SETUP when CPU supports 64-bit Long Mode
            //    and firmware is Legacy BIOS (on 32-bit IA32 UEFI tablets, firmware only boots 32-bit bootia32.efi).
            //  - 64-bit OS ('x64') -> 32-bit ISO ('x86'): Supported via bootable SWBI-SETUP on Legacy BIOS systems.
            bool cpuSupports64 = env.Hardware?.CpuSupports64Bit ?? true;
            bool isValid32To64Upgrade =
                string.Equals(env.HostArchitecture, "x86", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(iso.Architecture, "x64", StringComparison.OrdinalIgnoreCase) &&
                cpuSupports64 &&
                env.Firmware != FirmwareMode.Uefi;

            bool isValid64To32Legacy =
                string.Equals(env.HostArchitecture, "x64", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(iso.Architecture, "x86", StringComparison.OrdinalIgnoreCase) &&
                env.Firmware == FirmwareMode.LegacyBios;

            if (!exactArchMatch && !isValid32To64Upgrade && !isValid64To32Legacy)
            {
                string remediation = string.Equals(iso.Architecture, "x86", StringComparison.OrdinalIgnoreCase) && env.Firmware == FirmwareMode.Uefi
                    ? "64-bit UEFI firmware requires a 64-bit (x64) Windows ISO unless Legacy/CSM BIOS mode is enabled."
                    : string.Equals(env.HostArchitecture, "x86", StringComparison.OrdinalIgnoreCase) && !cpuSupports64
                        ? "This processor is 32-bit only; select a 32-bit (x86) Windows 10 ISO."
                        : $"Select a {env.HostArchitecture} Windows ISO image.";

                checks.Add(new SystemCheckItem
                {
                    Id = "iso-arch",
                    Category = "ISO",
                    Title = $"Architecture Mismatch (ISO: {iso.Architecture}, Host: {env.HostArchitecture})",
                    Severity = CheckSeverity.Blocker,
                    Summary = $"Selected ISO targets {iso.Architecture}, which cannot boot directly on this {env.HostArchitecture} ({env.Firmware}) system.",
                    RemediationHint = remediation
                });
            }
            else
            {
                string splitNote = iso.SplitStrategy switch
                {
                    WimSplitStrategy.DirectCopy => $"Payload ({iso.PayloadRelativePath}, {iso.PayloadSizeDisplay}) fits directly on FAT32.",
                    WimSplitStrategy.SplitWimToSwm => $"Payload ({iso.PayloadRelativePath}, {iso.PayloadSizeDisplay}) > 4 GB; will stream-split into {iso.SwmChunkSizeMb} MB .swm chunks in pure C# (zero DISM).",
                    WimSplitStrategy.ConvertEsdThenSplitToSwm => $"Solid ESD ({iso.PayloadRelativePath}, {iso.PayloadSizeDisplay}) > 4 GB; will convert and split into .swm chunks via self-hosted engine (zero DISM).",
                    _ => string.Empty
                };

                string crossBitNote = isValid32To64Upgrade
                    ? " [Cross-Bitness 32-bit → 64-bit Clean Upgrade supported via bootable SWBI-SETUP!]"
                    : isValid64To32Legacy
                        ? " [Cross-Bitness 64-bit → 32-bit Legacy BIOS Clean Install supported via bootable SWBI-SETUP!]"
                        : string.Empty;

                checks.Add(new SystemCheckItem
                {
                    Id = "iso-validation",
                    Category = "ISO",
                    Title = $"Validated {compatibility.TargetWindowsGeneration} ISO ({iso.Architecture}, {iso.FileSizeDisplay}, {compatibility.TransitionBadge})",
                    Severity = CheckSeverity.Pass,
                    Summary = $"{compatibility.TransitionHeadline}.{crossBitNote} {splitNote}"
                });
            }

            sizingPlan = _sizingCalculator.Calculate(
                iso,
                env.TargetOsPartition,
                env.ShrinkInfo,
                compatibility.DrivePlan.DriverReserveBytes);

            if (sizingPlan.FitsWithinFat32Limit && sizingPlan.FitsWithinShrinkLimit)
            {
                checks.Add(new SystemCheckItem
                {
                    Id = "shrink-space",
                    Category = "Space",
                    Title = $"Sufficient Shrinkable Space ({sizingPlan.InstallerSizeDisplay} Required incl. PC Drivers)",
                    Severity = CheckSeverity.Pass,
                    Summary =
                        $"OS partition shrinks from {sizingPlan.OsBeforeDisplay} to {sizingPlan.OsAfterDisplay}, creating a {sizingPlan.InstallerSizeDisplay} FAT32 installer partition while keeping {ByteFormatting.FormatGb(sizingPlan.RemainingOsFreeAfterShrinkBytes)} free on {env.SystemDriveLetter}:."
                });
            }
            else
            {
                string unmovableHint = env.ShrinkInfo is { UnmovableTailBytes: > ByteFormatting.OneGb }
                    ? $" ({ByteFormatting.FormatGb(env.ShrinkInfo.UnmovableTailBytes)} is pinned by unmovable clusters; {(targetDisk?.DriveKind == StorageDriveKind.Hdd ? "enable HDD tail consolidation or " : "")}temporarily run 'powercfg /h off')."
                    : string.Empty;

                checks.Add(new SystemCheckItem
                {
                    Id = "shrink-space",
                    Category = "Space",
                    Title = "Insufficient Shrinkable Partition Space",
                    Severity = CheckSeverity.Blocker,
                    Summary = (sizingPlan.BlockingReason ?? "Cannot shrink OS partition safely.") + unmovableHint,
                    RemediationHint = "Free disk space on C:, run Disk Cleanup, or temporarily disable hibernation (powercfg /h off) if unmovable files block shrinking."
                });
            }
        }

        return new ReadinessReport
        {
            Environment = env,
            Iso = iso,
            SizingPlan = sizingPlan,
            Compatibility = compatibility,
            Checks = checks,
            RequiresBitLockerRecoveryConfirmation = requiresBitLockerConfirmation
        };
    }

    /// <summary>
    /// Detects a Windows servicing operation that is waiting for a restart. Any of these pins files on C:,
    /// which is the most common reason a "large enough" shrink still fails.
    /// </summary>
    private static (bool Pending, string Reason) DetectPendingRestart()
    {
        try
        {
            using RegistryKey? sessionManager =
                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (sessionManager?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
            {
                return (true, "a file rename/replace is pending");
            }
        }
        catch
        {
            // Registry access denied or key missing: treat as unknown rather than blocking.
        }

        try
        {
            using RegistryKey? cbs = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs is not null)
            {
                return (true, "component servicing (CBS)");
            }
        }
        catch
        {
        }

        try
        {
            using RegistryKey? windowsUpdate = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (windowsUpdate is not null)
            {
                return (true, "Windows Update");
            }
        }
        catch
        {
        }

        return (false, string.Empty);
    }
}
