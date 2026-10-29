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

        // New manifests record exactly one boot mechanism. Never infer an unrelated entry
        // from the current system when that manifest deliberately left a field empty.
        bool recordedMode = manifest?.BootFirmwareMode is not null;
        string? loaderGuid = recordedMode
            ? manifest?.BootFirmwareMode == FirmwareMode.LegacyBios ? manifest.BcdOsLoaderGuid : null
            : manifest?.BcdOsLoaderGuid ?? bcdState.ExistingUsbFreeLoaderGuid;
        string? sdiGuid = recordedMode
            ? manifest?.BootFirmwareMode == FirmwareMode.LegacyBios ? manifest.BcdSdiDeviceGuid : null
            : manifest?.BcdSdiDeviceGuid ?? bcdState.ExistingUsbFreeSdiGuid;
        string? firmwareGuid = recordedMode
            ? manifest?.BootFirmwareMode == FirmwareMode.Uefi ? manifest.BcdFirmwareBootGuid : null
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
                    RemediationHint = $"Ensure the partition has a drive letter assigned so SWBI can verify '{SwbiBrand.ManifestFileName}'; SWBI never deletes a partition based on label alone."
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
            bool incompleteRecordedBoot = manifest?.BootFirmwareMode switch
            {
                FirmwareMode.Uefi => !UefiBootRegistration.TryParseFirmwareId(firmwareGuid, out _),
                FirmwareMode.LegacyBios => !Guid.TryParse(loaderGuid, out Guid loaderId) || loaderId == Guid.Empty ||
                    !Guid.TryParse(sdiGuid, out Guid sdiId) || sdiId == Guid.Empty || loaderId == sdiId,
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
                Summary = willExtendOs
                    ? $"[Dry-Run] Verified removal of '{SwbiBrand.BootChooserEntryName}' boot entries + '{SwbiBrand.PartitionVolumeLabel}' partition and extension of C: (+{inspection.Adjacency!.ReclaimableDisplay})."
                    : $"[Dry-Run] Verified removal of '{SwbiBrand.BootChooserEntryName}' boot entries + '{SwbiBrand.PartitionVolumeLabel}' partition (unallocated space left intact)."
            };
        }

        try
        {
            if (willRemoveBcd)
            {
                await _bcdBootService.RollbackBcdEntriesAsync(
                    inspection.OrphanedBcdLoaderGuid,
                    inspection.OrphanedBcdSdiGuid,
                    cancellationToken,
                    inspection.OrphanedBcdFirmwareGuid).ConfigureAwait(false);
            }

            if (willDeletePart && inspection.CandidatePartition is not null)
            {
                await _partitionService.DeleteInstallerPartitionAsync(
                    inspection.CandidatePartition.DiskNumber,
                    inspection.CandidatePartition.PartitionNumber,
                    inspection.CandidatePartition.PartitionGuid,
                    cancellationToken).ConfigureAwait(false);
            }

            if (willExtendOs)
            {
                await _partitionService.ExtendOsPartitionIntoReclaimedSpaceAsync(
                    inspection.CurrentOsDiskNumber,
                    inspection.CurrentOsPartitionNumber,
                    cancellationToken,
                    inspection.CurrentOsPartitionOffsetBytes).ConfigureAwait(false);
            }

            long reclaimed = willExtendOs ? (inspection.Adjacency?.ReclaimableBytes ?? 0L) : 0L;
            return new CleanupExecutionResult
            {
                Succeeded = true,
                Mode = ExecutionMode.Live,
                BcdEntriesRemoved = willRemoveBcd,
                PartitionDeleted = willDeletePart,
                OsPartitionExtended = willExtendOs,
                ReclaimedBytes = reclaimed,
                Summary = willExtendOs
                    ? $"Removed temporary {SwbiBrand.ShortName} installer resources and extended C: by {ByteFormatting.FormatGb(reclaimed)}."
                    : $"Removed temporary '{SwbiBrand.PartitionVolumeLabel}' partition and '{SwbiBrand.BootChooserEntryName}' boot entries. Freed space is now unallocated."
            };
        }
        catch (Exception ex)
        {
            _logger.Error("Cleanup", $"Cleanup execution failed: {ex.Message}");
            return new CleanupExecutionResult
            {
                Succeeded = false,
                Mode = ExecutionMode.Live,
                Summary = $"Cleanup encountered an error: {ex.Message}",
                ErrorMessage = ex.Message
            };
        }
    }

    private InstallerManifest? TryReadManifestFromPartition(PartitionSnapshot? candidate)
    {
        if (candidate?.DriveLetter is not { Length: > 0 } letter)
        {
            return null;
        }

        try
        {
            string root = $"{letter}:{Path.DirectorySeparatorChar}";
            string primaryPath = Path.Combine(root, SwbiBrand.ManifestFileName);
            string legacyPath = Path.Combine(root, SwbiBrand.LegacyManifestFileName);

            string? chosenPath = File.Exists(primaryPath)
                ? primaryPath
                : File.Exists(legacyPath)
                    ? legacyPath
                    : null;

            if (chosenPath is null)
            {
                return null;
            }

            string json = File.ReadAllText(chosenPath);
            return JsonSerializer.Deserialize<InstallerManifest>(json);
        }
        catch (Exception ex)
        {
            _logger.Warn("Cleanup", $"Failed to parse installer manifest on {letter}:: {ex.Message}");
            return null;
        }
    }
}
