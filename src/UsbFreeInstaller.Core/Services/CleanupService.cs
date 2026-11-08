using System.Text.Json;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services.Native;

namespace UsbFreeInstaller.Core.Services;

public sealed class CleanupService
{
    private const long MaxAlignmentGapBytes = 16L * ByteFormatting.OneMb;

    private readonly IDiskInspector _diskInspector;
    private readonly IPartitionService _partitionService;
    private readonly IBcdBootService _bcdBootService;
    private readonly ISafeLogger _logger;

    public CleanupService(
        IDiskInspector diskInspector,
        IPartitionService partitionService,
        IBcdBootService bcdBootService,
        ISafeLogger logger)
    {
        _diskInspector = diskInspector;
        _partitionService = partitionService;
        _bcdBootService = bcdBootService;
        _logger = logger;
    }

    public async Task<CleanupInspectionResult> InspectAsync(
        SystemEnvironmentSnapshot? providedSnapshot = null,
        InstallerManifest? simulatedManifest = null,
        CancellationToken cancellationToken = default)
    {
        SystemEnvironmentSnapshot env = providedSnapshot
            ?? await _diskInspector.InspectEnvironmentAsync(cancellationToken).ConfigureAwait(false);

        ActiveBcdState bcdState = env.IsSimulated
            ? new ActiveBcdState(
                false,
                $"partition={env.SystemDriveLetter}:",
                $"partition={env.SystemDriveLetter}:",
                simulatedManifest?.BcdOsLoaderGuid,
                simulatedManifest?.BcdSdiDeviceGuid,
                simulatedManifest?.BcdFirmwareBootGuid)
            : await _bcdBootService.InspectActiveBcdAsync(cancellationToken).ConfigureAwait(false);

        var checks = new List<SystemCheckItem>();
        DiskSnapshot? targetDisk = env.TargetDisk;
        PartitionSnapshot? osPart = env.TargetOsPartition;

        if (!bcdState.InspectionSucceeded)
        {
            checks.Add(new SystemCheckItem
            {
                Id = "cleanup-bcd-inspection",
                Category = "Boot",
                Title = "Active BCD Inspection Failed — No Cleanup Allowed",
                Severity = CheckSeverity.Blocker,
                Summary = "Could not inspect the active system BCD. Absence of entries cannot be assumed; no boot or partition changes are allowed.",
                RemediationHint = "Run SWBI as Administrator and inspect the log; do not delete any boot entries or partitions manually."
            });
            return new CleanupInspectionResult
            {
                State = CleanupSafetyState.BlockedActiveDependency,
                CurrentOsDiskNumber = env.TargetDiskNumber,
                CurrentOsPartitionNumber = env.TargetOsPartitionNumber,
                CurrentBootUsesRamdisk = bcdState.CurrentBootIsRamdisk,
                CurrentBootDependsOnInstaller = true,
                SafetyChecks = checks
            };
        }

        if (targetDisk is null || osPart is null)
        {
            checks.Add(new SystemCheckItem
            {
                Id = "cleanup-os-disk",
                Category = "Boot",
                Title = "Unable to Resolve Active Windows Partition",
                Severity = CheckSeverity.Blocker,
                Summary = "Could not identify the active Windows disk and OS partition."
            });

            return new CleanupInspectionResult
            {
                State = CleanupSafetyState.BlockedActiveDependency,
                CurrentOsDiskNumber = env.TargetDiskNumber,
                CurrentOsPartitionNumber = env.TargetOsPartitionNumber,
                CurrentBootUsesRamdisk = bcdState.CurrentBootIsRamdisk,
                CurrentBootDependsOnInstaller = true,
                SafetyChecks = checks
            };
        }

        PartitionSnapshot? candidate = targetDisk.Partitions
            .Where(p => p.PartitionNumber != osPart.PartitionNumber && !p.IsSystem && !p.IsBoot)
            .FirstOrDefault(p =>
                p.Role == PartitionRole.TemporaryInstaller ||
                SwbiBrand.IsRecognizedPartitionLabel(p.Label));

        InstallerManifest? manifest = simulatedManifest ?? TryReadManifestFromPartition(candidate);

        if (!bcdState.FirmwareEntryVerified)
        {
            checks.Add(new SystemCheckItem
            {
                Id = "cleanup-firmware-ownership",
                Category = "Boot",
                Title = "Named UEFI Entry Has an Unverifiable Device Path",
                Severity = CheckSeverity.Blocker,
                Summary = $"The old SWBI-named firmware slot {bcdState.ExistingFirmwareBootGuid} lacks a verifiable SWBI installer EFI path or falls outside SWBI's owned slot range. It will not be deleted based on its name alone.",
                RemediationHint = "Do not retry preparation or edit BootOrder/BootNext manually. Capture the read-only inspection and ask a trusted administrator to review this entry."
            });
            return new CleanupInspectionResult
            {
                State = CleanupSafetyState.BlockedAmbiguousPartition,
                CurrentOsDiskNumber = targetDisk.DiskNumber,
                CurrentOsPartitionNumber = osPart.PartitionNumber,
                CurrentBootUsesRamdisk = bcdState.CurrentBootIsRamdisk,
                CurrentBootDependsOnInstaller = bcdState.CurrentBootIsRamdisk,
                CandidatePartition = candidate,
                DiscoveredManifest = manifest,
                OrphanedBcdFirmwareGuid = bcdState.ExistingFirmwareBootGuid,
                SafetyChecks = checks
            };
        }

        // New manifests record exactly one boot mechanism. Never infer an unrelated entry
        // from the current system when that manifest deliberately left a field empty.
        bool recordedMode = manifest?.BootFirmwareMode is not null;
        // 1.3 UEFI manifests used a firmware BootNext slot. Newer UEFI manifests use the
        // backed-up system BCD ramdisk (like BIOS), so cleanup must accept *either* path,
        // never infer unrelated BCD entries, and never delete both for one manifest.
        bool uefiBcd = manifest?.BootFirmwareMode == FirmwareMode.Uefi &&
            string.IsNullOrWhiteSpace(manifest.BcdFirmwareBootGuid);
        bool usesBcd = manifest?.BootFirmwareMode == FirmwareMode.LegacyBios || uefiBcd;
        string? loaderGuid = recordedMode
            ? usesBcd ? manifest?.BcdOsLoaderGuid : null
            : manifest?.BcdOsLoaderGuid ?? bcdState.ExistingUsbFreeLoaderGuid;
        string? sdiGuid = recordedMode
            ? usesBcd ? manifest?.BcdSdiDeviceGuid : null
            : manifest?.BcdSdiDeviceGuid ?? bcdState.ExistingUsbFreeSdiGuid;
        string? firmwareGuid = recordedMode
            ? uefiBcd ? null : manifest?.BootFirmwareMode == FirmwareMode.Uefi ? manifest.BcdFirmwareBootGuid : null
            : manifest?.BcdFirmwareBootGuid ?? bcdState.ExistingFirmwareBootGuid;

        bool bootDependsOnCandidate = bcdState.CurrentBootIsRamdisk;
        if (candidate?.DriveLetter is { Length: > 0 } candLetter)
        {
            string marker = $"partition={candLetter}:";
            if (bcdState.CurrentDeviceRaw.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                bcdState.CurrentOsDeviceRaw.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                bootDependsOnCandidate = true;
            }
        }

        if (bootDependsOnCandidate)
        {
            checks.Add(new SystemCheckItem
            {
                Id = "cleanup-independent-boot",
                Category = "Boot",
                Title = "Active Session Still Depends on SWBI Installer / Ramdisk",
                Severity = CheckSeverity.Blocker,
                Summary = "Current boot session uses a ramdisk or references the SWBI installer partition. Complete installation and boot normally into the installed Windows OS first."
            });

            return new CleanupInspectionResult
            {
                State = CleanupSafetyState.BlockedActiveDependency,
                CurrentOsDiskNumber = targetDisk.DiskNumber,
                CurrentOsPartitionNumber = osPart.PartitionNumber,
                CurrentBootUsesRamdisk = bcdState.CurrentBootIsRamdisk,
                CurrentBootDependsOnInstaller = true,
                DiscoveredManifest = manifest,
                CandidatePartition = candidate,
                OrphanedBcdLoaderGuid = loaderGuid,
                OrphanedBcdSdiGuid = sdiGuid,
                OrphanedBcdFirmwareGuid = firmwareGuid,
                SafetyChecks = checks
            };
        }

        checks.Add(new SystemCheckItem
        {
            Id = "cleanup-independent-boot",
            Category = "Boot",
            Title = "Independent Windows Boot Confirmed",
            Severity = CheckSeverity.Pass,
            Summary = $"Active Windows OS is running independently from {targetDisk.DriveKindDisplay} (Disk {targetDisk.DiskNumber} Partition {osPart.PartitionNumber}, {env.SystemDriveLetter}:)."
        });

        if (candidate is null && loaderGuid is null && sdiGuid is null && firmwareGuid is null)
        {
            checks.Add(new SystemCheckItem
            {
                Id = "cleanup-nothing-found",
                Category = "Cleanup",
                Title = "No Temporary SWBI Installer Resources Found",
                Severity = CheckSeverity.Info,
                Summary = $"No '{SwbiBrand.PartitionVolumeLabel}' partition or temporary '{SwbiBrand.BootChooserEntryName}' boot entry was detected on this machine."
            });

            return new CleanupInspectionResult
            {
                State = CleanupSafetyState.NoInstallerArtifactsFound,
                CurrentOsDiskNumber = targetDisk.DiskNumber,
                CurrentOsPartitionNumber = osPart.PartitionNumber,
                CurrentBootUsesRamdisk = false,
                CurrentBootDependsOnInstaller = false,
                SafetyChecks = checks
            };
        }

        // Verify ownership via .swbi-installer-manifest.json (or legacy .usbfree-installer-manifest.json) + GPT GUID or MBR Signature/Offset
        if (candidate is not null)
        {
            bool guidOrMbrTokenMatches =
                !string.IsNullOrWhiteSpace(candidate.PartitionGuid) &&
                !string.IsNullOrWhiteSpace(manifest?.InstallerPartitionGuid) &&
                string.Equals(manifest.InstallerPartitionGuid, candidate.PartitionGuid, StringComparison.OrdinalIgnoreCase);

            bool mbrSizeAndDiskMatches =
                targetDisk.PartitionStyle == PartitionStyleType.Mbr &&
                manifest is not null &&
                manifest.DiskNumber == candidate.DiskNumber &&
                Math.Abs(manifest.InstallerPartitionBytes - candidate.SizeBytes) < 16L * 1024 * 1024;

            bool identityVerified = manifest is not null &&
                (targetDisk.PartitionStyle == PartitionStyleType.Gpt
                    ? guidOrMbrTokenMatches
                    : (guidOrMbrTokenMatches || mbrSizeAndDiskMatches));

            if (!identityVerified)
            {
                checks.Add(new SystemCheckItem
                {
                    Id = "cleanup-manifest-verify",
                    Category = "Safety",
                    Title = "Unverified Partition Identity — Refusing Automatic Deletion",
                    Severity = CheckSeverity.Blocker,
                    Summary =
                        $"Partition {candidate.PartitionNumber} is labeled '{candidate.Label}', but a matching '{SwbiBrand.ManifestFileName}' was not verified.",
                    RemediationHint = $"Inspect the exact volume and verify '{SwbiBrand.ManifestFileName}' and its GPT GUID or MBR disk signature/offset. SWBI reads matching volume-GUID paths without assigning a drive letter and never deletes a partition based on its label alone."
                });

                return new CleanupInspectionResult
                {
                    State = CleanupSafetyState.BlockedAmbiguousPartition,
                    CurrentOsDiskNumber = targetDisk.DiskNumber,
                    CurrentOsPartitionNumber = osPart.PartitionNumber,
                    CurrentBootUsesRamdisk = false,
                    CurrentBootDependsOnInstaller = false,
                    DiscoveredManifest = manifest,
                    CandidatePartition = candidate,
                    OrphanedBcdLoaderGuid = loaderGuid,
                    OrphanedBcdSdiGuid = sdiGuid,
                    OrphanedBcdFirmwareGuid = firmwareGuid,
                    SafetyChecks = checks
                };
            }

            bool requiresRecordedMode = manifest is not null &&
                (!Version.TryParse(manifest.SchemaVersion, out Version? schema) || schema >= new Version(1, 3));
            bool validBcdPair = Guid.TryParse(loaderGuid, out Guid loaderId) && loaderId != Guid.Empty &&
                Guid.TryParse(sdiGuid, out Guid sdiId) && sdiId != Guid.Empty && loaderId != sdiId;
            bool incompleteRecordedBoot = manifest?.BootFirmwareMode switch
            {
                FirmwareMode.Uefi when uefiBcd => !validBcdPair,
                FirmwareMode.Uefi => !UefiBootRegistration.TryParseFirmwareId(firmwareGuid, out _) ||
                    !string.IsNullOrWhiteSpace(manifest?.BcdOsLoaderGuid) ||
                    !string.IsNullOrWhiteSpace(manifest?.BcdSdiDeviceGuid),
                FirmwareMode.LegacyBios => !validBcdPair || !string.IsNullOrWhiteSpace(manifest?.BcdFirmwareBootGuid),
                not null => true,
                _ => requiresRecordedMode // Pre-1.3 manifests did not record a firmware mode.
            };
            if (incompleteRecordedBoot)
            {
                checks.Add(new SystemCheckItem
                {
                    Id = "cleanup-boot-identity",
                    Category = "Safety",
                    Title = "Incomplete Boot Identity — Refusing Automatic Deletion",
                    Severity = CheckSeverity.Blocker,
                    Summary = "The manifest does not contain a valid identity for the recorded firmware or BCD entry. The installer partition must be kept until boot references are inspected."
                });
                return new CleanupInspectionResult
                {
                    State = CleanupSafetyState.BlockedAmbiguousPartition,
                    CurrentOsDiskNumber = targetDisk.DiskNumber,
                    CurrentOsPartitionNumber = osPart.PartitionNumber,
                    CurrentBootUsesRamdisk = false,
                    CurrentBootDependsOnInstaller = false,
                    DiscoveredManifest = manifest,
                    CandidatePartition = candidate,
                    OrphanedBcdLoaderGuid = loaderGuid,
                    OrphanedBcdSdiGuid = sdiGuid,
                    OrphanedBcdFirmwareGuid = firmwareGuid,
                    SafetyChecks = checks
                };
            }

            checks.Add(new SystemCheckItem
            {
                Id = "cleanup-manifest-verify",
                Category = "Safety",
                Title = "Verified SWBI Manifest & Partition Identity",
                Severity = CheckSeverity.Pass,
                Summary = $"Confirmed Disk {candidate.DiskNumber} Partition {candidate.PartitionNumber} ('{candidate.Label}', {candidate.SizeDisplay}) was created by {SwbiBrand.BrandedTitle}."
            });
        }

        if (loaderGuid is not null || sdiGuid is not null || firmwareGuid is not null)
        {
            checks.Add(new SystemCheckItem
            {
                Id = "cleanup-bcd-entry",
                Category = "Boot",
                Title = $"Temporary '{SwbiBrand.BootChooserEntryName}' Boot Entries Detected",
                Severity = CheckSeverity.Info,
                Summary = $"Will remove BCD loader {loaderGuid ?? "(none)"}, SDI {sdiGuid ?? "(none)"}, and UEFI F12 entry {firmwareGuid ?? "(none)"}."
            });
        }

        if (candidate is null)
        {
            checks.Add(new SystemCheckItem
            {
                Id = "cleanup-boot-only",
                Category = "Recovery",
                Title = "Orphaned Boot Entry — No Installer Partition",
                Severity = CheckSeverity.Warning,
                Summary = "No SWBI-SETUP partition was found on the Windows disk. Cleanup will only remove the identified SWBI boot entries; C: and all partitions stay untouched.",
                RemediationHint = "Review the identifiers and confirm Windows boots independently. SWBI saves a fresh active-BCD backup and checks BCD type/title and WinPE flags or the older UEFI option's title/device path before removal. No manifest remains for cryptographic proof of an old firmware slot; if anything is unfamiliar, stop and inspect the log."
            });
        }

        AdjacencyAnalysis? adjacency = candidate is not null
            ? AnalyzeAdjacency(targetDisk, osPart, candidate)
            : null;

        if (adjacency is not null)
        {
            checks.Add(new SystemCheckItem
            {
                Id = "cleanup-adjacency",
                Category = "Space",
                Title = adjacency.CanExtendOsPartitionDirectly
                    ? $"C: Can Reclaim {adjacency.ReclaimableDisplay} Automatically"
                    : "Intervening Partition Prevents Direct C: Expansion",
                Severity = adjacency.CanExtendOsPartitionDirectly ? CheckSeverity.Pass : CheckSeverity.Warning,
                Summary = adjacency.Explanation
            });
        }

        return new CleanupInspectionResult
        {
            State = CleanupSafetyState.ReadyToClean,
            CurrentOsDiskNumber = targetDisk.DiskNumber,
            CurrentOsPartitionNumber = osPart.PartitionNumber,
            CurrentOsPartitionOffsetBytes = osPart.OffsetBytes,
            CurrentBootUsesRamdisk = false,
            CurrentBootDependsOnInstaller = false,
            DiscoveredManifest = manifest,
            CandidatePartition = candidate,
            OrphanedBcdLoaderGuid = loaderGuid,
            OrphanedBcdSdiGuid = sdiGuid,
            OrphanedBcdFirmwareGuid = firmwareGuid,
            Adjacency = adjacency,
            SafetyChecks = checks
        };
    }

    public static AdjacencyAnalysis AnalyzeAdjacency(
        DiskSnapshot disk,
        PartitionSnapshot osPartition,
        PartitionSnapshot installerPartition)
    {
        string partName = string.IsNullOrWhiteSpace(installerPartition.Label)
            ? SwbiBrand.PartitionVolumeLabel
            : installerPartition.Label;

        if (installerPartition.OffsetBytes < osPartition.EndOffsetBytes)
        {
            return new AdjacencyAnalysis
            {
                CanExtendOsPartitionDirectly = false,
                OsPartitionNumber = osPartition.PartitionNumber,
                InstallerPartitionNumber = installerPartition.PartitionNumber,
                ReclaimableBytes = installerPartition.SizeBytes,
                Explanation =
                    $"The '{partName}' partition sits at a lower disk offset (to the left) of the current Windows partition. Basic NTFS volumes can only be extended into contiguous space immediately to their right."
            };
        }

        var intervening = disk.Partitions
            .Where(p => p.PartitionNumber != osPartition.PartitionNumber &&
                        p.PartitionNumber != installerPartition.PartitionNumber &&
                        p.OffsetBytes >= osPartition.EndOffsetBytes &&
                        p.OffsetBytes < installerPartition.OffsetBytes)
            .OrderBy(p => p.OffsetBytes)
            .ToArray();

        if (intervening.Length > 0)
        {
            string summary = string.Join(", ", intervening.Select(p => $"Partition {p.PartitionNumber} ({p.Role}, {p.SizeDisplay})"));
            return new AdjacencyAnalysis
            {
                CanExtendOsPartitionDirectly = false,
                OsPartitionNumber = osPartition.PartitionNumber,
                InstallerPartitionNumber = installerPartition.PartitionNumber,
                ReclaimableBytes = installerPartition.SizeBytes,
                InterveningPartitionsSummary = summary,
                Explanation =
                    $"Deleting '{partName}' frees {installerPartition.SizeDisplay} of unallocated space, but {summary} sits between C: and '{partName}' (commonly created by Windows Setup for WinRE). Windows cannot natively extend C: across an intervening partition."
            };
        }

        long gapBytes = installerPartition.OffsetBytes - osPartition.EndOffsetBytes;
        bool contiguous = gapBytes <= MaxAlignmentGapBytes;

        return new AdjacencyAnalysis
        {
            CanExtendOsPartitionDirectly = true,
            OsPartitionNumber = osPartition.PartitionNumber,
            InstallerPartitionNumber = installerPartition.PartitionNumber,
            ReclaimableBytes = installerPartition.SizeBytes + Math.Max(0L, gapBytes),
            Explanation = contiguous
                ? $"'{partName}' ({installerPartition.SizeDisplay}) is immediately adjacent to the right of your Windows partition (Partition {osPartition.PartitionNumber}). C: can be extended automatically after deletion."
                : $"'{partName}' ({installerPartition.SizeDisplay}) follows Partition {osPartition.PartitionNumber} with {ByteFormatting.FormatGb(gapBytes)} of unallocated space in between — both can be merged into C:."
        };
    }

    public async Task<CleanupExecutionResult> ExecuteCleanupAsync(
        CleanupInspectionResult inspection,
        bool extendOsPartitionIfAdjacent,
        ExecutionMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!inspection.CanExecuteCleanup)
        {
            return new CleanupExecutionResult
            {
                Succeeded = false,
                Mode = mode,
                Summary = "Cleanup cannot proceed in the current safety state.",
                ErrorMessage = $"Safety state is {inspection.State}."
            };
        }

        bool willRemoveBcd = inspection.OrphanedBcdLoaderGuid is not null ||
                             inspection.OrphanedBcdSdiGuid is not null ||
                             inspection.OrphanedBcdFirmwareGuid is not null;
        bool willDeletePart = inspection.CandidatePartition is not null;
        bool willExtendOs = extendOsPartitionIfAdjacent &&
                            willDeletePart &&
                            inspection.Adjacency is { CanExtendOsPartitionDirectly: true };

        if (mode == ExecutionMode.DryRun)
        {
            _logger.Info(
                "Cleanup",
                $"[Dry-Run] Would remove BCD/UEFI={willRemoveBcd}, DeletePartition={willDeletePart}, ExtendOs={willExtendOs}.");

            return new CleanupExecutionResult
            {
                Succeeded = true,
                Mode = ExecutionMode.DryRun,
                BcdEntriesRemoved = willRemoveBcd,
                PartitionDeleted = willDeletePart,
                OsPartitionExtended = willExtendOs,
                ReclaimedBytes = willExtendOs ? (inspection.Adjacency?.ReclaimableBytes ?? 0L) : 0L,
                Summary = !willDeletePart
                    ? "[Dry-Run] Would remove the identified SWBI boot entries only; no installer partition exists and C: would not be changed."
                    : willExtendOs
                        ? $"[Dry-Run] Would remove '{SwbiBrand.BootChooserEntryName}' boot entries + '{SwbiBrand.PartitionVolumeLabel}' partition and extend C: (+{inspection.Adjacency!.ReclaimableDisplay})."
                        : $"[Dry-Run] Would remove '{SwbiBrand.BootChooserEntryName}' boot entries + '{SwbiBrand.PartitionVolumeLabel}' partition (unallocated space left intact)."
            };
        }

        bool bootEntriesRemoved = false;
        bool deletionAttempted = false;
        bool partitionDeleted = false;
        bool osExtended = false;
        try
        {
            // The user may have left this screen open while another operation changed BCD
            // or disk layout. Re-inspect from the actual machine immediately before mutation;
            // an old orphaned GUID must never be replayed against a new boot entry.
            SystemEnvironmentSnapshot currentEnv = await _diskInspector.InspectEnvironmentAsync(cancellationToken).ConfigureAwait(false);
            CleanupInspectionResult current = await InspectAsync(currentEnv,
                currentEnv.IsSimulated ? inspection.DiscoveredManifest : null,
                cancellationToken).ConfigureAwait(false);
            PartitionSnapshot? expectedPart = inspection.CandidatePartition;
            PartitionSnapshot? currentPart = current.CandidatePartition;
            bool samePartition = expectedPart is null && currentPart is null ||
                expectedPart is not null && currentPart is not null &&
                expectedPart.DiskNumber == currentPart.DiskNumber &&
                expectedPart.PartitionNumber == currentPart.PartitionNumber &&
                expectedPart.OffsetBytes == currentPart.OffsetBytes &&
                expectedPart.SizeBytes == currentPart.SizeBytes &&
                string.Equals(expectedPart.PartitionGuid, currentPart.PartitionGuid, StringComparison.OrdinalIgnoreCase);
            bool sameBoot = string.Equals(inspection.OrphanedBcdLoaderGuid, current.OrphanedBcdLoaderGuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(inspection.OrphanedBcdSdiGuid, current.OrphanedBcdSdiGuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(inspection.OrphanedBcdFirmwareGuid, current.OrphanedBcdFirmwareGuid, StringComparison.OrdinalIgnoreCase);
            if (!current.CanExecuteCleanup || !samePartition || !sameBoot ||
                current.CurrentOsDiskNumber != inspection.CurrentOsDiskNumber ||
                current.CurrentOsPartitionNumber != inspection.CurrentOsPartitionNumber ||
                current.CurrentOsPartitionOffsetBytes != inspection.CurrentOsPartitionOffsetBytes)
            {
                _logger.Warn("Cleanup", "The boot/disk state changed since inspection; refusing to remove stale boot entries or partitions.");
                return new CleanupExecutionResult
                {
                    Succeeded = false,
                    Mode = ExecutionMode.Live,
                    Summary = "Boot or disk state changed since the last inspection. Nothing was removed; inspect again before retrying.",
                    ErrorMessage = "Stale cleanup inspection."
                };
            }

            if (willRemoveBcd)
            {
                await _bcdBootService.RollbackBcdEntriesAsync(
                    inspection.OrphanedBcdLoaderGuid,
                    inspection.OrphanedBcdSdiGuid,
                    cancellationToken,
                    inspection.OrphanedBcdFirmwareGuid).ConfigureAwait(false);
                bootEntriesRemoved = true;
            }

            if (willDeletePart && inspection.CandidatePartition is not null)
            {
                deletionAttempted = true;
                await _partitionService.DeleteInstallerPartitionAsync(
                    inspection.CandidatePartition.DiskNumber,
                    inspection.CandidatePartition.PartitionNumber,
                    inspection.CandidatePartition.PartitionGuid,
                    cancellationToken).ConfigureAwait(false);
                partitionDeleted = true;
            }

            if (willExtendOs)
            {
                await _partitionService.ExtendOsPartitionIntoReclaimedSpaceAsync(
                    inspection.CurrentOsDiskNumber,
                    inspection.CurrentOsPartitionNumber,
                    cancellationToken,
                    inspection.CurrentOsPartitionOffsetBytes).ConfigureAwait(false);
                osExtended = true;
            }

            long reclaimed = osExtended ? (inspection.Adjacency?.ReclaimableBytes ?? 0L) : 0L;
            return new CleanupExecutionResult
            {
                Succeeded = true,
                Mode = ExecutionMode.Live,
                BcdEntriesRemoved = bootEntriesRemoved,
                PartitionDeleted = partitionDeleted,
                OsPartitionExtended = osExtended,
                ReclaimedBytes = reclaimed,
                Summary = !willDeletePart
                    ? "Removed the identified SWBI boot entries only. No partition was deleted and C: was not changed. Re-inspect before preparing again."
                    : osExtended
                        ? $"Removed temporary {SwbiBrand.ShortName} installer resources and extended C: by {ByteFormatting.FormatGb(reclaimed)}."
                        : $"Removed temporary '{SwbiBrand.PartitionVolumeLabel}' partition and '{SwbiBrand.BootChooserEntryName}' boot entries. Freed space is now unallocated."
            };
        }
        catch (Exception ex)
        {
            _logger.Error("Cleanup", $"Cleanup stopped (boot removed: {bootEntriesRemoved}; partition deleted: {partitionDeleted}; " +
                $"C: extended: {osExtended}): {ex.Message}");
            string stage = !willDeletePart
                ? "Boot-only cleanup stopped. No partition was changed; some boot entries may already have been removed. Re-inspect before retrying. "
                : partitionDeleted
                ? $"The {SwbiBrand.PartitionVolumeLabel} partition WAS deleted, but C: extension failed. " +
                  "Space may be unallocated or the partition may be larger than its NTFS filesystem. " +
                  "Inspect both in Disk Management; do not delete any more partitions."
                : deletionAttempted
                    ? (bootEntriesRemoved ? "The boot entry was removed, but deletion" : "Deletion") +
                      $" of {SwbiBrand.PartitionVolumeLabel} failed or could not be verified. " +
                      "The partition may remain or already be unallocated; inspect the current layout before retrying."
                    : $"Cleanup stopped before deleting {SwbiBrand.PartitionVolumeLabel}; the partition remains on disk. Re-inspect before retrying.";
            return new CleanupExecutionResult
            {
                Succeeded = false,
                Mode = ExecutionMode.Live,
                BcdEntriesRemoved = bootEntriesRemoved,
                PartitionDeleted = partitionDeleted,
                OsPartitionExtended = osExtended,
                Summary = $"{stage} Error: {ex.Message}",
                ErrorMessage = ex.Message
            };
        }
    }

    private InstallerManifest? TryReadManifestFromPartition(PartitionSnapshot? candidate)
    {
        if (candidate is null) return null;

        // A GPT NO_DRIVE_LETTER attribute may survive a reboot. Read only a volume whose
        // physical disk AND start offset match the inspected candidate; do not assign a
        // new drive letter or treat a same-label/same-size partition as an ownership record.
        var roots = new List<string>();
        if (OperatingSystem.IsWindows() && candidate.OffsetBytes > 0)
        {
            foreach (RawVolumeBinding volume in Win32StorageInterop.EnumerateAllVolumes())
            {
                if (volume.DiskNumber == candidate.DiskNumber &&
                    volume.StartingOffsetBytes == candidate.OffsetBytes)
                    roots.Add(volume.VolumeGuidPath);
            }
        }
        if (candidate.DriveLetter is { Length: > 0 } letter)
            roots.Add($"{letter}:{Path.DirectorySeparatorChar}");

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                InstallerManifest? manifest = ReadManifestFromRoot(root);
                if (manifest is not null) return manifest;
            }
            catch (Exception ex)
            {
                _logger.Warn("Cleanup", $"Failed to read installer manifest on {root}: {ex.Message}");
            }
        }
        return null;
    }

    internal static InstallerManifest? ReadManifestFromRoot(string root)
    {
        string primary = Path.Combine(root, SwbiBrand.ManifestFileName);
        string legacy = Path.Combine(root, SwbiBrand.LegacyManifestFileName);
        string? path = File.Exists(primary) ? primary : File.Exists(legacy) ? legacy : null;
        if (path is null) return null;

        if (new FileInfo(path).Length > 1024 * 1024)
            throw new InvalidDataException("Installer manifest exceeds the maximum supported size (1 MiB).");
        return JsonSerializer.Deserialize<InstallerManifest>(File.ReadAllText(path));
    }
}
