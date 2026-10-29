namespace UsbFreeInstaller.Core.Models;

public enum ExecutionMode
{
    DryRun = 0,
    Live = 1
}

public enum SetupPartitionAction
{
    PreserveSystem = 0,
    DeleteOrFormatTarget = 1,
    PreserveInstallerSource = 2,
    PreserveOther = 3
}

public sealed record SetupPartitionRow
{
    public required int DiskNumber { get; init; }
    public required int PartitionNumber { get; init; }
    public required string SetupName { get; init; }
    public required string TypeLabel { get; init; }
    public required long SizeBytes { get; init; }
    public required SetupPartitionAction Action { get; init; }
    public required string ActionBadgeText { get; init; }
    public required string Explanation { get; init; }

    public string SizeDisplay => ByteFormatting.FormatGb(SizeBytes);
}

public sealed record SetupPartitionGuide
{
    public required int TargetDiskNumber { get; init; }
    public required string TargetDiskModel { get; init; }
    public required IReadOnlyList<SetupPartitionRow> Rows { get; init; }
    public required IReadOnlyList<string> CriticalRules { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record InstallerManifest
{
    public const string ManifestFileName = SwbiBrand.ManifestFileName;
    public const string LegacyManifestFileName = SwbiBrand.LegacyManifestFileName;
    public const string DefaultVolumeLabel = SwbiBrand.PartitionVolumeLabel;
    public const string BcdEntryDescription = SwbiBrand.BootChooserEntryName;
    public const string BcdSdiDescription = SwbiBrand.BcdSdiDeviceName;

    public string SchemaVersion { get; init; } = "1.3";
    public FirmwareMode? BootFirmwareMode { get; init; }
    public string ToolName { get; init; } = SwbiBrand.AsciiTitle;
    public string ToolVersion { get; init; } = SwbiBrand.CurrentVersion;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string SourceIsoFileName { get; init; }
    public string? SourceIsoSha256 { get; init; }
    public required int DiskNumber { get; init; }
    public string DiskUniqueId { get; init; } = string.Empty;
    public StorageDriveKind DriveKind { get; init; }
    public WindowsTransitionKind TransitionKind { get; init; }
    public InstallWorkflowChoice WorkflowChoice { get; init; }
    public bool ExportedHostDrivers { get; init; }
    public bool InjectedBootWimStorageDrivers { get; init; }
    public required int InstallerPartitionNumber { get; init; }
    public required string InstallerPartitionGuid { get; init; }
    public long InstallerPartitionBytes { get; init; }
    public string VolumeLabel { get; init; } = DefaultVolumeLabel;
    public string FileSystem { get; init; } = "FAT32";
    public WimSplitStrategy SplitStrategy { get; init; }
    public string? InstallerDriveLetterAtPrep { get; init; }
    public string? BcdOsLoaderGuid { get; init; }
    public string? BcdSdiDeviceGuid { get; init; }
    public string? BcdFirmwareBootGuid { get; init; }
    public string? BcdBackupPath { get; init; }
}

public enum PreparationStepState
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    SkippedDryRun = 3,
    Failed = 4,
    RolledBack = 5
}

public sealed record PreparationProgressUpdate
{
    public required int StepIndex { get; init; }
    public required int TotalSteps { get; init; }
    public required string StepTitle { get; init; }
    public required string Detail { get; init; }
    public required int Percent { get; init; }
    public required PreparationStepState State { get; init; }
}

public sealed record UserSafetyConfirmations
{
    public bool HasVerifiedExternalBackup { get; init; }
    public bool UnderstandsCleanInstallWipesTargetPartition { get; init; }
    public bool UnderstandsNeverDeleteInstallerPartition { get; init; }
    public bool HasSavedBitLockerRecoveryKey { get; init; }
    public bool AutoSuspendBitLockerForNextReboot { get; init; } = true;

    public bool AreMandatoryConfirmationsSatisfied(bool requiresBitLockerConfirmation) =>
        HasVerifiedExternalBackup &&
        UnderstandsCleanInstallWipesTargetPartition &&
        UnderstandsNeverDeleteInstallerPartition &&
        (!requiresBitLockerConfirmation || HasSavedBitLockerRecoveryKey);
}

public sealed record PreparationResult
{
    public required bool Succeeded { get; init; }
    public required ExecutionMode Mode { get; init; }
    public required string Summary { get; init; }
    public InstallerManifest? CreatedManifest { get; init; }
    public SetupPartitionGuide? SetupGuide { get; init; }
    public string? LogFilePath { get; init; }
    public IReadOnlyList<string> PlannedOrExecutedActions { get; init; } = Array.Empty<string>();
    public string? ErrorMessage { get; init; }
}
