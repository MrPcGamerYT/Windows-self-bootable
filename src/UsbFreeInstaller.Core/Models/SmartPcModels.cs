namespace UsbFreeInstaller.Core.Models;

public enum WindowsTransitionKind
{
    Unknown = 0,
    CleanReinstallSameBuild = 1,
    UpgradeToNewerWindows = 2,
    DowngradeToOlderWindows = 3
}

public enum InstallWorkflowChoice
{
    /// <summary>
    /// Boot into dedicated SWBI-SETUP partition; delete/format old Windows partition and install fresh.
    /// Works universally for clean reinstalls, major upgrades, AND downgrades ("degrades").
    /// </summary>
    SelfBootableCleanInstall = 0,

    /// <summary>
    /// Boot into dedicated SWBI-SETUP partition; select old Windows partition WITHOUT formatting so existing files move to C:\Windows.old.
    /// Ideal for downgrades or reinstalls when keeping a local Windows.old copy alongside external backups.
    /// </summary>
    SelfBootablePreserveWindowsOld = 1,

    /// <summary>
    /// Extract ISO to SWBI-SETUP local partition (with bootable fallback) and launch setup.exe /auto upgrade to keep apps, files, and settings.
    /// Supported only when TargetBuild &gt;= CurrentBuild and architectures match.
    /// </summary>
    InPlaceUpgradeKeepAppsAndFiles = 2
}

public sealed record CurrentWindowsInfo
{
    public string ProductName { get; init; } = "Windows";
    public string EditionId { get; init; } = "Professional";
    public string DisplayVersion { get; init; } = string.Empty;
    public int BuildNumber { get; init; }
    public string InstallLanguage { get; init; } = "en-US";
    public bool CanRollbackToPreviousOs { get; init; }

    public string SummaryDisplay =>
        BuildNumber > 0
            ? $"{ProductName} ({(string.IsNullOrWhiteSpace(DisplayVersion) ? "Build" : DisplayVersion)}, Build {BuildNumber})"
            : ProductName;
}

public sealed record PcHardwareCapabilities
{
    public string CpuName { get; init; } = "x64 Processor";
    public int LogicalCores { get; init; } = 4;
    public long TotalRamBytes { get; init; } = 8L * ByteFormatting.OneGb;
    public bool TpmPresent { get; init; }
    public string TpmVersion { get; init; } = "Unknown";
    public bool CpuSupports64Bit { get; init; } = true;

    public string RamDisplay => ByteFormatting.FormatGb(TotalRamBytes);

    public bool HasTpm20 =>
        TpmPresent && TpmVersion.StartsWith("2.", StringComparison.OrdinalIgnoreCase);

    public string SummaryDisplay =>
        $"{CpuName} ({LogicalCores} threads)  •  RAM: {RamDisplay}  •  TPM: {(TpmPresent ? TpmVersion : "Not Detected")}";
}

public sealed record DriveOptimizationPlan
{
    public required StorageDriveKind DriveKind { get; init; }
    public required string DriveSummary { get; init; }
    public required int CopyBufferSizeBytes { get; init; }
    public required bool RecommendPreShrinkConsolidation { get; init; }
    public required bool SupportsTrim { get; init; }
    public required bool AutoExportDriversToWinPe { get; init; }
    public required bool AutoInjectDriversIntoBootWim { get; init; }
    public required long DriverReserveBytes { get; init; }
    public required string TuningExplanation { get; init; }
}

public sealed record SmartTailoringOptions
{
    public InstallWorkflowChoice WorkflowChoice { get; init; } = InstallWorkflowChoice.SelfBootableCleanInstall;
    public bool ExportHostStorageAndNetworkDrivers { get; init; } = true;
    public bool InjectBootCriticalStorageDriversIntoBootWim { get; init; }
    public bool WriteEiCfgToUnlockAllEditions { get; init; } = true;
    public bool BypassWin11HardwareChecks { get; init; }
    public bool RunDriveAwarePreShrinkOptimization { get; init; }
}

public sealed record SmartCompatibilityReport
{
    public required WindowsTransitionKind TransitionKind { get; init; }
    public required string TargetWindowsGeneration { get; init; }
    public required int CurrentBuildNumber { get; init; }
    public required int TargetBuildNumber { get; init; }
    public required string TransitionBadge { get; init; }
    public required string TransitionHeadline { get; init; }
    public required string TransitionExplanation { get; init; }
    public required bool SupportsInPlaceKeepAppsUpgrade { get; init; }
    public int? MatchedEditionIndex { get; init; }
    public string? MatchedEditionName { get; init; }
    public required DriveOptimizationPlan DrivePlan { get; init; }
    public required SmartTailoringOptions RecommendedOptions { get; init; }
    public required IReadOnlyList<SystemCheckItem> CompatibilityAdvisories { get; init; }
}
