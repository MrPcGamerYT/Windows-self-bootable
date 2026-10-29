namespace UsbFreeInstaller.Core.Models;

public enum CleanupSafetyState
{
    NoInstallerArtifactsFound = 0,
    ReadyToClean = 1,
    BlockedActiveDependency = 2,
    BlockedAmbiguousPartition = 3
}

public sealed record AdjacencyAnalysis
{
    public required bool CanExtendOsPartitionDirectly { get; init; }
    public required int OsPartitionNumber { get; init; }
    public required int InstallerPartitionNumber { get; init; }
    public required long ReclaimableBytes { get; init; }
    public string InterveningPartitionsSummary { get; init; } = string.Empty;
    public required string Explanation { get; init; }

    public string ReclaimableDisplay => ByteFormatting.FormatGb(ReclaimableBytes);
}

public sealed record CleanupInspectionResult
{
    public required CleanupSafetyState State { get; init; }
    public required int CurrentOsDiskNumber { get; init; }
    public required int CurrentOsPartitionNumber { get; init; }

    /// <summary>
    /// Start offset of the running OS partition captured during inspection. Cleanup re-verifies this before
    /// resizing anything, because partition numbers can shift once the installer partition is deleted.
    /// </summary>
    public long CurrentOsPartitionOffsetBytes { get; init; }
    public required bool CurrentBootUsesRamdisk { get; init; }
    public required bool CurrentBootDependsOnInstaller { get; init; }
    public InstallerManifest? DiscoveredManifest { get; init; }
    public PartitionSnapshot? CandidatePartition { get; init; }
    public string? OrphanedBcdLoaderGuid { get; init; }
    public string? OrphanedBcdSdiGuid { get; init; }
    public string? OrphanedBcdFirmwareGuid { get; init; }
    public AdjacencyAnalysis? Adjacency { get; init; }
    public IReadOnlyList<SystemCheckItem> SafetyChecks { get; init; } = Array.Empty<SystemCheckItem>();

    public bool CanExecuteCleanup => State == CleanupSafetyState.ReadyToClean;
}

public sealed record CleanupExecutionResult
{
    public required bool Succeeded { get; init; }
    public required ExecutionMode Mode { get; init; }
    public bool BcdEntriesRemoved { get; init; }
    public bool PartitionDeleted { get; init; }
    public bool OsPartitionExtended { get; init; }
    public long ReclaimedBytes { get; init; }
    public required string Summary { get; init; }
    public string? ErrorMessage { get; init; }
}
