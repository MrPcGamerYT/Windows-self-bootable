namespace UsbFreeInstaller.Core.Models;

public enum CheckSeverity
{
    Pass = 0,
    Info = 1,
    Warning = 2,
    Blocker = 3
}

public enum BitLockerProtectionState
{
    Unknown = 0,
    Off = 1,
    On = 2
}

public enum BitLockerConversionState
{
    Unknown = 0,
    FullyDecrypted = 1,
    FullyEncrypted = 2,
    EncryptionInProgress = 3,
    DecryptionInProgress = 4,
    EncryptionPaused = 5,
    DecryptionPaused = 6
}

public sealed record BitLockerStatusInfo
{
    public required string VolumeLetter { get; init; }
    public required BitLockerProtectionState ProtectionState { get; init; }
    public required BitLockerConversionState ConversionState { get; init; }
    public required bool HasRecoveryPasswordProtector { get; init; }
    public string KeyProtectorSummary { get; init; } = string.Empty;

    public bool IsEncryptedOrActive =>
        ProtectionState == BitLockerProtectionState.On ||
        ConversionState != BitLockerConversionState.FullyDecrypted;

    public bool IsConversionInFlight =>
        ConversionState is BitLockerConversionState.EncryptionInProgress or BitLockerConversionState.DecryptionInProgress;
}

public sealed record PowerStatusInfo
{
    public required bool IsOnAcPower { get; init; }
    public required bool HasBattery { get; init; }
    public int? BatteryChargePercent { get; init; }
}

public sealed record SystemCheckItem
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required CheckSeverity Severity { get; init; }
    public required string Summary { get; init; }
    public string? RemediationHint { get; init; }
}

public sealed record PartitionSizingPlan
{
    /// <summary>
    /// SWBI's own validated ceiling for a FAT32 installer partition (32,000 MB, just under the classic 32 GiB
    /// FAT32 boundary that fastfat and most UEFI implementations expect). This is intentionally a SWBI policy
    /// limit with a safety margin, not a limitation of SWBI's built-in formatter.
    /// </summary>
    public const long MaxNativeFat32PartitionMb = 32_000L;

    /// <summary>
    /// Minimum sensible installer partition size (8,192 MB = 8 GB).
    /// </summary>
    public const long MinInstallerPartitionMb = 8_192L;

    public required long IsoSizeBytes { get; init; }
    public required long PayloadSizeBytes { get; init; }
    public long DriverReserveBytes { get; init; }
    public required WimSplitStrategy SplitStrategy { get; init; }
    public required int EstimatedSwmCount { get; init; }
    public required long RequiredTempOsBytes { get; init; }
    public required long InstallerPartitionBytes { get; init; }
    public required long InstallerPartitionMb { get; init; }
    public required long OsPartitionBeforeBytes { get; init; }
    public required long OsPartitionAfterBytes { get; init; }
    public required long RemainingOsFreeAfterShrinkBytes { get; init; }
    public required bool FitsWithinShrinkLimit { get; init; }
    public required bool FitsWithinFat32Limit { get; init; }
    public string? BlockingReason { get; init; }

    public string InstallerSizeDisplay => ByteFormatting.FormatGb(InstallerPartitionBytes);
    public string OsBeforeDisplay => ByteFormatting.FormatGb(OsPartitionBeforeBytes);
    public string OsAfterDisplay => ByteFormatting.FormatGb(OsPartitionAfterBytes);
}

public sealed record ReadinessReport
{
    public required SystemEnvironmentSnapshot Environment { get; init; }
    public IsoInspectionResult? Iso { get; init; }
    public PartitionSizingPlan? SizingPlan { get; init; }
    public SmartCompatibilityReport? Compatibility { get; init; }
    public required IReadOnlyList<SystemCheckItem> Checks { get; init; }
    public required bool RequiresBitLockerRecoveryConfirmation { get; init; }
    public DateTimeOffset EvaluatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public int BlockerCount => Checks.Count(c => c.Severity == CheckSeverity.Blocker);
    public int WarningCount => Checks.Count(c => c.Severity == CheckSeverity.Warning);

    public bool CanProceedInLiveMode =>
        Iso is { IsValid: true } &&
        SizingPlan is { FitsWithinShrinkLimit: true, FitsWithinFat32Limit: true } &&
        BlockerCount == 0 &&
        Environment.IsElevated &&
        !Environment.IsSimulated;

    public bool CanSimulateDryRun =>
        Iso is { IsValid: true } &&
        SizingPlan is { FitsWithinShrinkLimit: true, FitsWithinFat32Limit: true } &&
        Checks.Where(c => c.Id != "admin-elevation").All(c => c.Severity != CheckSeverity.Blocker);
}
