using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services.Native;

namespace UsbFreeInstaller.Core.Services;

public sealed class PreparationOrchestrator
{
    private readonly IDiskInspector _diskInspector;
    private readonly IPartitionService _partitionService;
    private readonly IIsoDeploymentService _deploymentService;
    private readonly IBcdBootService _bcdBootService;
    private readonly IDriverHarvesterService? _driverHarvester;
    private readonly ReadinessEvaluator _readinessEvaluator;
    private readonly SetupGuideGenerator _guideGenerator;
    private readonly ISafeLogger _logger;
    private readonly string _appDataDirectory;

    public PreparationOrchestrator(
        IDiskInspector diskInspector,
        IPartitionService partitionService,
        IIsoDeploymentService deploymentService,
        IBcdBootService bcdBootService,
        ReadinessEvaluator readinessEvaluator,
        SetupGuideGenerator guideGenerator,
        ISafeLogger logger,
        string? appDataDirectory = null,
        IDriverHarvesterService? driverHarvesterService = null)
    {
        _diskInspector = diskInspector;
        _partitionService = partitionService;
        _deploymentService = deploymentService;
        _bcdBootService = bcdBootService;
        _driverHarvester = driverHarvesterService;
        _readinessEvaluator = readinessEvaluator;
        _guideGenerator = guideGenerator;
        _logger = logger;
        _appDataDirectory = !string.IsNullOrWhiteSpace(appDataDirectory)
            ? appDataDirectory
            : SwbiWorkDirectory.EnsureSafeWorkDirectory("appdata");
    }

    public async Task<PreparationResult> ExecuteAsync(
        ReadinessReport initialReport,
        UserSafetyConfirmations confirmations,
        ExecutionMode mode,
        IProgress<PreparationProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default,
        SmartTailoringOptions? tailoringOptions = null)
    {
        const int totalSteps = 7;
        var executedActions = new List<string>();

        if (initialReport.Iso is null || initialReport.SizingPlan is null)
        {
            return FailResult(mode, "A validated Windows ISO and partition sizing plan are required.", executedActions);
        }

        if (!confirmations.AreMandatoryConfirmationsSatisfied(initialReport.RequiresBitLockerRecoveryConfirmation))
        {
            return FailResult(
                mode,
                "All required backup, partition-safety, and BitLocker recovery confirmations must be acknowledged before continuing.",
                executedActions);
        }

        SmartTailoringOptions options = tailoringOptions
            ?? initialReport.Compatibility?.RecommendedOptions
            ?? new SmartTailoringOptions();

        if (options.WorkflowChoice == InstallWorkflowChoice.InPlaceUpgradeKeepAppsAndFiles &&
            initialReport.Compatibility is { SupportsInPlaceKeepAppsUpgrade: false })
        {
            return FailResult(
                mode,
                $"In-Place Upgrade (keeping apps) is not supported when downgrading ('degrading') to an older Windows build. Select {SwbiBrand.ShortName} Self-Bootable Clean Install or Preserve Windows.old mode instead.",
                executedActions);
        }

        if (mode == ExecutionMode.DryRun)
        {
            if (!initialReport.CanSimulateDryRun)
            {
                return FailResult(
                    mode,
                    "Readiness checks reported blocking issues. Resolve all blockers before running preparation.",
                    executedActions);
            }

            return await ExecuteDryRunSimulationAsync(initialReport, confirmations, options, progress, cancellationToken).ConfigureAwait(false);
        }

        if (!initialReport.CanProceedInLiveMode)
        {
            return FailResult(
                mode,
                "Live execution is blocked: ensure SWBI is running as Administrator on real supported hardware with zero blocking checks.",
                executedActions);
        }

        Report(progress, 1, totalSteps, "Re-verifying live PC & drive layout", "Checking target disk, drive geometry, and shrink bounds...", 5, PreparationStepState.Running);
        SystemEnvironmentSnapshot freshEnv = await _diskInspector.InspectEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        ReadinessReport freshReport = _readinessEvaluator.Evaluate(freshEnv, initialReport.Iso);

        if (!freshReport.CanProceedInLiveMode || freshReport.SizingPlan is null)
        {
            return FailResult(
                mode,
                "Pre-flight re-verification failed because disk state changed since the last scan. Please review updated readiness checks.",
                executedActions);
        }

        PartitionSnapshot targetOsPart = freshEnv.TargetOsPartition
            ?? throw new InvalidOperationException("Target OS partition could not be resolved during pre-flight verification.");
        DiskSnapshot targetDisk = freshEnv.TargetDisk
            ?? throw new InvalidOperationException("Target OS disk could not be resolved during pre-flight verification.");
        PartitionSizingPlan plan = freshReport.SizingPlan;
        DriveOptimizationPlan drivePlan = freshReport.Compatibility?.DrivePlan
            ?? new SmartCompatibilityAnalyzer().BuildDriveOptimizationPlan(targetDisk, freshEnv.ShrinkInfo);

        CreatedInstallerPartitionInfo? createdPartition = null;
        BcdProvisionResult? createdBcd = null;

        try
        {
            // Step 2: Suspend BitLocker + Drive-Aware Pre-Shrink Optimization (HDD defrag vs SSD/NVMe ReTrim)
            if (freshEnv.BitLocker is { IsEncryptedOrActive: true } && confirmations.AutoSuspendBitLockerForNextReboot)
            {
                Report(progress, 2, totalSteps, "Suspending BitLocker for 1 reboot", $"Suspending BitLocker protection on {freshEnv.SystemDriveLetter}:...", 10, PreparationStepState.Running);
                await _partitionService.SuspendBitLockerForNextRebootAsync(freshEnv.SystemDriveLetter, cancellationToken).ConfigureAwait(false);
                executedActions.Add($"Suspended BitLocker protection on {freshEnv.SystemDriveLetter}: for 1 reboot.");
            }

            if (options.RunDriveAwarePreShrinkOptimization)
            {
                Report(progress, 2, totalSteps, $"Optimizing {targetDisk.DriveKindDisplay} before shrink", $"Running drive-safe optimization pass on {freshEnv.SystemDriveLetter}:...", 14, PreparationStepState.Running);
                await _partitionService.OptimizeOsVolumeBeforeShrinkAsync(freshEnv.SystemDriveLetter, targetDisk.DriveKind, cancellationToken).ConfigureAwait(false);
                executedActions.Add($"Tail-cluster evacuation for {freshEnv.SystemDriveLetter}: will run inside the {targetDisk.DriveKindDisplay} shrink transaction (no separate optimization pass).");
            }

            // Step 3: Shrink OS partition and create FAT32 SWBI-SETUP installer partition
            Report(
                progress,
                3,
                totalSteps,
                $"Shrinking C: & creating '{SwbiBrand.PartitionVolumeLabel}' partition",
                $"Resizing {targetDisk.DriveKindDisplay} (Disk {targetDisk.DiskNumber} Partition {targetOsPart.PartitionNumber}) to {plan.OsAfterDisplay} and creating {plan.InstallerSizeDisplay} FAT32 '{SwbiBrand.PartitionVolumeLabel}'...",
                22,
                PreparationStepState.Running);

            createdPartition = await _partitionService.ShrinkOsAndCreateFat32InstallerPartitionAsync(
                diskNumber: targetDisk.DiskNumber,
                osPartitionNumber: targetOsPart.PartitionNumber,
                expectedOsPartitionGuid: targetOsPart.PartitionGuid,
                newOsPartitionSizeBytes: plan.OsPartitionAfterBytes,
                installerPartitionSizeBytes: plan.InstallerPartitionBytes,
                volumeLabel: SwbiBrand.PartitionVolumeLabel,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            executedActions.Add(
                $"Shrank Disk {targetDisk.DiskNumber} Partition {targetOsPart.PartitionNumber} and created FAT32 '{SwbiBrand.PartitionVolumeLabel}' partition {createdPartition.DriveLetter}: ({ByteFormatting.FormatGb(createdPartition.SizeBytes)}).");

            // Step 4: Extract ISO, split >4 GB WIM/ESD into .swm chunks, and apply SWBI drive icon + autorun branding
            var deployProgress = new Progress<DeploymentProgress>(dp =>
            {
                int mappedPct = 28 + ((dp.Percent * 38) / 100);
                Report(progress, 4, totalSteps, dp.Phase, dp.CurrentItem, mappedPct, PreparationStepState.Running);
            });

            await _deploymentService.DeployIsoToInstallerPartitionAsync(
                freshReport.Iso!,
                createdPartition.DriveLetter,
                drivePlan.CopyBufferSizeBytes,
                options.WriteEiCfgToUnlockAllEditions,
                deployProgress,
                cancellationToken,
                bypassWin11HardwareChecks: options.BypassWin11HardwareChecks).ConfigureAwait(false);

            executedActions.Add(
                $"Extracted ISO '{Path.GetFileName(freshReport.Iso!.IsoPath)}' + '{SwbiBrand.AsciiTitle}' drive icon/branding to {createdPartition.DriveLetter}: ({SwbiBrand.PartitionVolumeLabel}, {drivePlan.CopyBufferSizeBytes / 1024:N0} KB {targetDisk.DriveKindDisplay} buffer, {freshReport.Iso.SplitStrategy}).");

            // Step 5: Harvest PC's active Storage & Network drivers into $WinPEDriver$ AND 'SWBI - Self Windows Bootable Installer Drivers'
            bool exportedDrivers = false;
            bool injectedBootWim = false;
            if (_driverHarvester is not null && (options.ExportHostStorageAndNetworkDrivers || options.InjectBootCriticalStorageDriversIntoBootWim))
            {
                var drvProgress = new Progress<DeploymentProgress>(dp =>
                {
                    int mappedPct = 68 + ((dp.Percent * 12) / 100);
                    Report(progress, 5, totalSteps, dp.Phase, dp.CurrentItem, mappedPct, PreparationStepState.Running);
                });

                DriverHarvestSummary harvest = await _driverHarvester.ExportAndInjectDriversAsync(
                    createdPartition.DriveLetter,
                    includeNetworkDrivers: options.ExportHostStorageAndNetworkDrivers,
                    injectStorageIntoBootWim: options.InjectBootCriticalStorageDriversIntoBootWim,
                    progress: drvProgress,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                exportedDrivers = (harvest.StorageDriverInfCount + harvest.NetworkDriverInfCount) > 0;
                injectedBootWim = harvest.InjectedIntoBootWim;
                executedActions.Add(
                    $"Exported {harvest.StorageDriverInfCount} storage/chipset + {harvest.NetworkDriverInfCount} Wi-Fi/Ethernet driver package(s) to $WinPEDriver$ and '{createdPartition.DriveLetter}:\\{SwbiBrand.DriverChooserFolderName}' (boot.wim injected: {injectedBootWim}).");
            }

            // Step 6: Verify boot.wim, boot.sdi, EFI bootloader, and install.wim/.swm/.esd BEFORE modifying BCD
            Report(
                progress,
                6,
                totalSteps,
                "Verifying SWBI installation media integrity",
                $"Validating boot.wim, boot.sdi, EFI files, and Windows image on {createdPartition.DriveLetter}: ({SwbiBrand.PartitionVolumeLabel})...",
                84,
                PreparationStepState.Running);

            await _deploymentService.VerifyDeployedBootAndInstallFilesAsync(
                freshReport.Iso,
                createdPartition.DriveLetter,
                cancellationToken).ConfigureAwait(false);

            executedActions.Add($"Verified WinPE boot files and DISM image readability on {createdPartition.DriveLetter}: ({SwbiBrand.PartitionVolumeLabel}).");

            // Step 7: Use exactly one one-time mechanism for the detected firmware mode.
            Report(
                progress,
                7,
                totalSteps,
                $"Registering '{SwbiBrand.BootChooserEntryName}' boot choosers",
                "Registering the firmware-specific boot path and writing the SWBI manifest...",
                93,
                PreparationStepState.Running);

            string bcdBackupDir = Path.Combine(_appDataDirectory, "BcdBackups");
            FirmwareMode effectiveBootMode = freshEnv.Firmware;
            createdBcd = await _bcdBootService.ConfigureOneTimeWinPeBootAsync(
                createdPartition.DriveLetter,
                bcdBackupDir,
                effectiveBootMode,
                cancellationToken).ConfigureAwait(false);

            executedActions.Add(effectiveBootMode == FirmwareMode.Uefi
                ? $"Registered '{SwbiBrand.BootChooserEntryName}' in UEFI firmware ({createdBcd.FirmwareBootGuid}); host BCD was not modified."
                : $"Registered legacy Windows Boot Manager entry {createdBcd.OsLoaderGuid} (BCD backup: '{createdBcd.BcdBackupFilePath}').");

            var manifest = new InstallerManifest
            {
                BootFirmwareMode = effectiveBootMode,
                SourceIsoFileName = Path.GetFileName(freshReport.Iso.IsoPath),
                SourceIsoSha256 = freshReport.Iso.Sha256Hex,
                DiskNumber = createdPartition.DiskNumber,
                DiskUniqueId = targetDisk.UniqueId,
                DriveKind = targetDisk.DriveKind,
                TransitionKind = freshReport.Compatibility?.TransitionKind ?? WindowsTransitionKind.CleanReinstallSameBuild,
                WorkflowChoice = options.WorkflowChoice,
                ExportedHostDrivers = exportedDrivers,
                InjectedBootWimStorageDrivers = injectedBootWim,
                InstallerPartitionNumber = createdPartition.PartitionNumber,
                InstallerPartitionGuid = createdPartition.PartitionGuid,
                InstallerPartitionBytes = createdPartition.SizeBytes,
                VolumeLabel = SwbiBrand.PartitionVolumeLabel,
                FileSystem = "FAT32",
                SplitStrategy = freshReport.Iso.SplitStrategy,
                InstallerDriveLetterAtPrep = createdPartition.DriveLetter,
                BcdOsLoaderGuid = createdBcd.OsLoaderGuid,
                BcdSdiDeviceGuid = createdBcd.SdiDeviceGuid,
                BcdFirmwareBootGuid = createdBcd.FirmwareBootGuid,
                BcdBackupPath = createdBcd.BcdBackupFilePath
            };

            await _partitionService.WriteInstallerManifestAsync(
                createdPartition.DriveLetter,
                manifest,
                cancellationToken).ConfigureAwait(false);

            // From here on the machine is already prepared. Anything that fails is reported as a warning and
            // must NEVER fall through to the rollback path, which would delete a working installer partition
            // and leave C: shrunk with no installation source.
            if (options.WorkflowChoice == InstallWorkflowChoice.InPlaceUpgradeKeepAppsAndFiles && _driverHarvester is not null)
            {
                try
                {
                    await _driverHarvester.LaunchInPlaceUpgradeSetupAsync(createdPartition.DriveLetter, cancellationToken).ConfigureAwait(false);
                    executedActions.Add($"Launched In-Place Upgrade Setup ({createdPartition.DriveLetter}:\\setup.exe /auto upgrade) while keeping '{SwbiBrand.PartitionVolumeLabel}' armed as bootable recovery fallback.");
                }
                catch (Exception launchError)
                {
                    _logger.Warn("Orchestrator", $"In-Place Upgrade Setup could not be launched: {launchError.Message}");
                    executedActions.Add(
                        "WARNING: '" + createdPartition.DriveLetter + ":\\setup.exe /auto upgrade' could not be started (" + launchError.Message +
                        "). The installer partition is ready — run setup.exe manually to start the upgrade.");
                }
            }

            SetupPartitionGuide guide;
            try
            {
                guide = _guideGenerator.Generate(freshEnv, plan);
            }
            catch (Exception guideError)
            {
                // A formatting/edge-case failure in guide generation must not undo a completed preparation.
                _logger.Warn("Orchestrator", $"Setup guide generation failed: {guideError.Message}");
                guide = new SetupPartitionGuide
                {
                    TargetDiskNumber = targetDisk.DiskNumber,
                    TargetDiskModel = targetDisk.FriendlyName,
                    Rows = Array.Empty<SetupPartitionRow>(),
                    CriticalRules = new[]
                    {
                        $"SWBI prepared '{SwbiBrand.PartitionVolumeLabel}' on Disk {targetDisk.DiskNumber}, but the detailed partition cheat sheet could not be generated ({guideError.Message}).",
                        $"In Windows Setup, identify the installer partition by its label '{SwbiBrand.PartitionVolumeLabel}' and size, and NEVER delete it.",
                        $"After the new Windows boots, run {SwbiBrand.BrandedTitle} in Post-Install Cleanup mode to remove the temporary partition and boot entry."
                    }
                };
            }

            Report(
                progress,
                7,
                totalSteps,
                $"{SwbiBrand.BrandedTitle} is armed & ready",
                "Review and save your SWBI Boot & Partition Cheat Sheet before restarting.",
                100,
                PreparationStepState.Completed);

            return new PreparationResult
            {
                Succeeded = true,
                Mode = ExecutionMode.Live,
                Summary =
                    $"Prepared {ByteFormatting.FormatGb(createdPartition.SizeBytes)} '{SwbiBrand.PartitionVolumeLabel}' ({createdPartition.DriveLetter}: on {targetDisk.DriveKindDisplay}) and registered '{SwbiBrand.BootChooserEntryName}' in boot choosers for {freshReport.Compatibility?.TransitionHeadline ?? "Windows Setup"}.",
                CreatedManifest = manifest,
                SetupGuide = guide,
                LogFilePath = _logger.LogFilePath,
                PlannedOrExecutedActions = executedActions
            };
        }
        catch (Exception ex)
        {
            _logger.Error("Orchestrator", $"Live preparation failed: {ex.Message}. Attempting cleanup where safe...");
            bool keepPartition = ex is FirmwareBootProvisionException { CleanupIncomplete: true };
            if (keepPartition && createdPartition is not null)
            {
                executedActions.Add("Kept the installer partition because boot-configuration cleanup could not be verified.");
            }
            await RollbackFailedPreparationAsync(createdBcd, keepPartition ? null : createdPartition,
                targetDisk.DiskNumber, targetOsPart.PartitionNumber, executedActions).ConfigureAwait(false);

            Report(progress, totalSteps, totalSteps, "Preparation stopped; inspect cleanup status",
                ex.Message, 100, PreparationStepState.Failed);

            return FailResult(ExecutionMode.Live,
                $"Preparation stopped. Cleanup was attempted where safe and may require manual inspection: {ex.Message}",
                executedActions);
        }
    }

    private async Task<PreparationResult> ExecuteDryRunSimulationAsync(
        ReadinessReport report,
        UserSafetyConfirmations confirmations,
        SmartTailoringOptions options,
        IProgress<PreparationProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        SystemEnvironmentSnapshot env = report.Environment;
        IsoInspectionResult iso = report.Iso!;
        PartitionSizingPlan plan = report.SizingPlan!;
        DiskSnapshot disk = env.TargetDisk!;
        PartitionSnapshot osPart = env.TargetOsPartition!;
        DriveOptimizationPlan drivePlan = report.Compatibility?.DrivePlan
            ?? new SmartCompatibilityAnalyzer().BuildDriveOptimizationPlan(disk, env.ShrinkInfo);

        var planned = new List<string>();
        const int totalSteps = 7;

        Report(progress, 1, totalSteps, "[Dry-Run] Validating PC hardware, drive type & transition", "No disk changes will be written in Dry-Run mode.", 14, PreparationStepState.SkippedDryRun);
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        planned.Add($"[Dry-Run] {SwbiBrand.BrandedTitle} • Target Drive: {disk.DriveKindDisplay} ({disk.FriendlyName}, {disk.SectorGeometryDisplay}) • Mode: {report.Compatibility?.TransitionBadge ?? "CLEAN"} ({options.WorkflowChoice}).");

        if (env.BitLocker is { IsEncryptedOrActive: true } && confirmations.AutoSuspendBitLockerForNextReboot)
        {
            planned.Add($"[Dry-Run] Suspend-BitLocker -MountPoint \"{env.SystemDriveLetter}:\" -RebootCount 1");
        }

        if (options.RunDriveAwarePreShrinkOptimization)
        {
            planned.Add($"[Dry-Run] NativeNtfsShrinkEngine: Evacuate movable tail clusters on {env.SystemDriveLetter}: via FSCTL_GET_VOLUME_BITMAP + FSCTL_MOVE_FILE");
        }

        Report(progress, 3, totalSteps, $"[Dry-Run] Simulating OS shrink & '{SwbiBrand.PartitionVolumeLabel}' creation", $"Would shrink {env.SystemDriveLetter}: to {plan.OsAfterDisplay} and create {plan.InstallerSizeDisplay} FAT32 '{SwbiBrand.PartitionVolumeLabel}'.", 35, PreparationStepState.SkippedDryRun);
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        planned.Add($"[Dry-Run] Native Win32 FSCTL_SHRINK_VOLUME + IOCTL_DISK_GROW_PARTITION on Disk {disk.DiskNumber} Partition {osPart.PartitionNumber} -> {plan.OsPartitionAfterBytes} bytes");
        planned.Add($"[Dry-Run] Native Win32 IOCTL_DISK_SET_DRIVE_LAYOUT_EX + SetVolumeMountPointW + Pure C# NativeFat32Formatter ({plan.InstallerPartitionBytes} bytes, label '{SwbiBrand.PartitionVolumeLabel}')");

        Report(progress, 4, totalSteps, "[Dry-Run] Simulating drive-tuned ISO extraction & SWBI drive branding", $"Buffer: {drivePlan.CopyBufferSizeBytes / 1024:N0} KB • Strategy: {iso.SplitStrategy}.", 58, PreparationStepState.SkippedDryRun);
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        planned.Add($"[Dry-Run] Pure C# IsoImageStreamReader: Extract '{Path.GetFileName(iso.IsoPath)}' to {SwbiBrand.PartitionVolumeLabel}:\\ ({drivePlan.CopyBufferSizeBytes / 1024:N0} KB buffer) + write swbi.ico & autorun.inf ('{SwbiBrand.AsciiTitle}').");
        if (iso.SplitStrategy == WimSplitStrategy.SplitWimToSwm)
        {
            planned.Add($"[Dry-Run] PureCSharpWimSplitter: Stream '<ISO>\\sources\\install.wim' -> 'W:\\sources\\install.swm' ({iso.SwmChunkSizeMb} MB chunks, 0 DLLs, 0 temp space on C:).");
        }
        else if (iso.SplitStrategy == WimSplitStrategy.ConvertEsdThenSplitToSwm)
        {
            planned.Add($"[Dry-Run] ISO-Self-Hosted wimgapi.dll + PureCSharpWimSplitter: Convert ESD -> WIM -> 'W:\\sources\\install.swm' ({iso.SwmChunkSizeMb} MB chunks).");
        }

        if (options.WriteEiCfgToUnlockAllEditions)
        {
            planned.Add("[Dry-Run] Write W:\\sources\\ei.cfg ([Channel] Retail) if none exists to unlock all editions for Upgrade / Downgrade.");
        }

        if (options.BypassWin11HardwareChecks)
        {
            planned.Add("[Dry-Run] Pure C# AutoUnattend.xml + Offline WinPE RegLoadKeyW: Apply Windows 11 LabConfig Hardware Bypass (TPM/SecureBoot/CPU/RAM/Storage).");
        }

        if (options.ExportHostStorageAndNetworkDrivers)
        {
            planned.Add($"[Dry-Run] Pure C# NativeDriverStoreScanner (+ live INF/System32 stripped-OS self-heal): Export OEM Storage & Network drivers -> W:\\$WinPEDriver$\\ AND 'W:\\{SwbiBrand.DriverChooserFolderName}\\'.");
        }

        if (options.InjectBootCriticalStorageDriversIntoBootWim)
        {
            planned.Add($"[Dry-Run] ISO-Self-Hosted wimgapi.dll: Stage OEM Storage Controller driver ('{disk.ControllerServiceName}') into W:\\sources\\boot.wim.");
        }

        string bootPlan = env.Firmware == FirmwareMode.Uefi
            ? "UEFI BootNext points to the installer's EFI loader; host BCD is not modified."
            : "Legacy Windows Boot Manager uses a backed-up BCD and one-time boot sequence.";
        Report(progress, 6, totalSteps, "[Dry-Run] Simulating firmware-specific boot registration", bootPlan, 85, PreparationStepState.SkippedDryRun);
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        planned.Add("[Dry-Run] Pure C# binary WIM/SWM verification on boot\\boot.sdi, sources\\boot.wim, EFI bootloaders, and spanned SWM GUID/part headers.");
        planned.Add(env.Firmware == FirmwareMode.Uefi
            ? $"[Dry-Run] Register UEFI BootXXXX + BootOrder + BootNext for '{SwbiBrand.BootChooserEntryName}'; no host or standalone BCD writes."
            : $"[Dry-Run] Back up the system BCD and create one legacy Windows Boot Manager entry for '{SwbiBrand.BootChooserEntryName}'.");
        planned.Add($"[Dry-Run] Write W:\\{SwbiBrand.ManifestFileName} for post-install cleanup verification.");

        if (options.WorkflowChoice == InstallWorkflowChoice.InPlaceUpgradeKeepAppsAndFiles)
        {
            planned.Add("[Dry-Run] Launch W:\\setup.exe /auto upgrade (In-Place Upgrade keeping apps & files).");
        }

        SetupPartitionGuide guide = _guideGenerator.Generate(env, plan);

        var simulatedManifest = new InstallerManifest
        {
            SourceIsoFileName = Path.GetFileName(iso.IsoPath),
            SourceIsoSha256 = iso.Sha256Hex,
            DiskNumber = disk.DiskNumber,
            DiskUniqueId = disk.UniqueId,
            DriveKind = disk.DriveKind,
            TransitionKind = report.Compatibility?.TransitionKind ?? WindowsTransitionKind.CleanReinstallSameBuild,
            WorkflowChoice = options.WorkflowChoice,
            ExportedHostDrivers = options.ExportHostStorageAndNetworkDrivers,
            InjectedBootWimStorageDrivers = options.InjectBootCriticalStorageDriversIntoBootWim,
            InstallerPartitionNumber = osPart.PartitionNumber + 1,
            InstallerPartitionGuid = "{00000000-DRY0-RUN0-0000-000000000001}",
            InstallerPartitionBytes = plan.InstallerPartitionBytes,
            VolumeLabel = SwbiBrand.PartitionVolumeLabel,
            SplitStrategy = iso.SplitStrategy,
            BootFirmwareMode = env.Firmware,
            BcdOsLoaderGuid = env.Firmware == FirmwareMode.LegacyBios ? "{00000000-0000-0000-0000-000000000002}" : null,
            BcdSdiDeviceGuid = env.Firmware == FirmwareMode.LegacyBios ? "{00000000-0000-0000-0000-000000000003}" : null,
            BcdFirmwareBootGuid = env.Firmware == FirmwareMode.Uefi ? "UEFI:Boot0010" : null
        };

        foreach (string item in planned)
        {
            _logger.Info("DryRun", item);
        }

        Report(progress, 7, totalSteps, "SWBI Dry-Run simulation finished (0 bytes modified)", "Simulation only: no disk, boot configuration, or firmware changes were made.", 100, PreparationStepState.Completed);

        return new PreparationResult
        {
            Succeeded = true,
            Mode = ExecutionMode.DryRun,
            Summary =
                $"SWBI Dry-Run complete ({report.Compatibility?.TransitionBadge ?? "READY"}): Validated {disk.DriveKindDisplay} tuning, {plan.InstallerSizeDisplay} FAT32 '{SwbiBrand.PartitionVolumeLabel}' partition, '{SwbiBrand.BootChooserEntryName}' boot choosers, and PC driver harvesting.",
            CreatedManifest = simulatedManifest,
            SetupGuide = guide,
            LogFilePath = _logger.LogFilePath,
            PlannedOrExecutedActions = planned
        };
    }

    private async Task RollbackFailedPreparationAsync(
        BcdProvisionResult? createdBcd,
        CreatedInstallerPartitionInfo? createdPartition,
        int diskNumber,
        int osPartitionNumber,
        List<string> executedActions)
    {
        bool bootCleanupSucceeded = true;
        if (createdBcd is not null)
        {
            try
            {
                await _bcdBootService.RollbackBcdEntriesAsync(
                    createdBcd.OsLoaderGuid,
                    createdBcd.SdiDeviceGuid,
                    CancellationToken.None,
                    createdBcd.FirmwareBootGuid).ConfigureAwait(false);
                executedActions.Add($"Removed the temporary {SwbiBrand.ShortName} boot entry.");
            }
            catch (Exception bcdEx)
            {
                bootCleanupSucceeded = false;
                executedActions.Add("Kept the installer partition because boot-entry cleanup failed.");
                _logger.Error("Rollback", $"Boot-entry rollback failed: {bcdEx.Message}");
            }
        }

        if (createdPartition is not null && bootCleanupSucceeded)
        {
            try
            {
                await _partitionService.DeleteInstallerPartitionAsync(
                    createdPartition.DiskNumber,
                    createdPartition.PartitionNumber,
                    createdPartition.PartitionGuid,
                    CancellationToken.None).ConfigureAwait(false);
                executedActions.Add($"Removed incomplete '{SwbiBrand.PartitionVolumeLabel}' partition (Disk {createdPartition.DiskNumber} Partition {createdPartition.PartitionNumber}).");

                await _partitionService.ExtendOsPartitionIntoReclaimedSpaceAsync(
                    diskNumber,
                    osPartitionNumber,
                    CancellationToken.None).ConfigureAwait(false);
                executedActions.Add("Attempted to extend the OS partition into the reclaimed space.");
            }
            catch (Exception partEx)
            {
                _logger.Error("Rollback", $"Partition rollback warning: {partEx.Message}");
            }
        }
    }

    private PreparationResult FailResult(ExecutionMode mode, string error, IReadOnlyList<string> actions)
    {
        _logger.Error("Orchestrator", error);
        return new PreparationResult
        {
            Succeeded = false,
            Mode = mode,
            Summary = error,
            LogFilePath = _logger.LogFilePath,
            PlannedOrExecutedActions = actions,
            ErrorMessage = error
        };
    }

    private static void Report(
        IProgress<PreparationProgressUpdate>? progress,
        int step,
        int total,
        string title,
        string detail,
        int percent,
        PreparationStepState state)
    {
        progress?.Report(new PreparationProgressUpdate
        {
            StepIndex = step,
            TotalSteps = total,
            StepTitle = title,
            Detail = detail,
            Percent = percent,
            State = state
        });
    }
}
