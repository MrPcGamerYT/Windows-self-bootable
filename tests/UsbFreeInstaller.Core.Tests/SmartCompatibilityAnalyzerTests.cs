using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Simulation;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class SmartCompatibilityAnalyzerTests
{
    private readonly SmartCompatibilityAnalyzer _analyzer = new();

    [Theory]
    [InlineData("SATA", "HDD", "storahci", "Seagate BarraCuda 1TB", StorageDriveKind.Hdd)]
    [InlineData("SATA", "SSD", "storahci", "Crucial MX500", StorageDriveKind.SataSsd)]
    [InlineData("NVMe", "SSD", "stornvme", "Samsung SSD 990 PRO", StorageDriveKind.NvmeSsd)]
    [InlineData("RAID", "SSD", "iaStorVD", "WD SN850X", StorageDriveKind.RaidOrVmd)]
    [InlineData("NVMe", "SSD", "rcraid", "AMD NVMe Array", StorageDriveKind.RaidOrVmd)]
    [InlineData("MMC", "Unspecified", "sdbus", "SanDisk iNAND 64GB", StorageDriveKind.EmmcOrUfs)]
    [InlineData("SAS", "Unspecified", "vioscsi", "QEMU VirtIO Disk", StorageDriveKind.VirtualDisk)]
    public void ClassifyDriveKind_RecognizesAllDriveTypesAndControllers(
        string busType,
        string mediaType,
        string controllerSvc,
        string model,
        StorageDriveKind expected)
    {
        StorageDriveKind actual = SmartCompatibilityAnalyzer.ClassifyDriveKind(busType, mediaType, controllerSvc, model);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Analyze_IntelVmdWin10ToWin11Scenario_DetectsUpgradeAndAutoEnablesBootWimDriverInjection()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateIntelVmdUpgradeWin10ToWin11Scenario();
        SmartCompatibilityReport report = _analyzer.Analyze(scenario.Environment, scenario.SampleIso);

        Assert.Equal(WindowsTransitionKind.UpgradeToNewerWindows, report.TransitionKind);
        Assert.True(report.SupportsInPlaceKeepAppsUpgrade);
        Assert.Equal("Windows 11 Home", report.MatchedEditionName); // Core -> Windows 11 Home
        Assert.True(report.DrivePlan.AutoInjectDriversIntoBootWim);
        Assert.True(report.RecommendedOptions.InjectBootCriticalStorageDriversIntoBootWim);
    }

    [Fact]
    public void Analyze_SataHddWin11ToWin10Scenario_DetectsDowngradeAndTunesForSpinningHardDrive()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateSataHddDowngradeWin11ToWin10Scenario();
        SmartCompatibilityReport report = _analyzer.Analyze(scenario.Environment, scenario.SampleIso);

        Assert.Equal(WindowsTransitionKind.DowngradeToOlderWindows, report.TransitionKind);
        Assert.False(report.SupportsInPlaceKeepAppsUpgrade);
        Assert.Equal("Windows 10 Pro", report.MatchedEditionName); // Professional -> Windows 10 Pro
        Assert.Equal(StorageDriveKind.Hdd, report.DrivePlan.DriveKind);
        Assert.Equal(4 * 1024 * 1024, report.DrivePlan.CopyBufferSizeBytes); // 4 MB sequential buffer for HDD
        Assert.False(report.DrivePlan.SupportsTrim);
        Assert.True(report.DrivePlan.RecommendPreShrinkConsolidation);
    }

    [Fact]
    public void ReadinessEvaluator_LegacyMbrWithSeparateSystemReservedTargetingWin10_IsAllowed()
    {
        SimulationScenario win10Hdd = SimulatedEnvironmentProvider.CreateSataHddDowngradeWin11ToWin10Scenario();
        const long mb = ByteFormatting.OneMb;
        const long gb = ByteFormatting.OneGb;

        var sysReserved = new PartitionSnapshot
        {
            DiskNumber = 0,
            PartitionNumber = 1,
            Label = "System Reserved",
            FileSystem = "NTFS",
            OffsetBytes = 1L * mb,
            SizeBytes = 500L * mb,
            Role = PartitionRole.SystemReservedLegacy,
            IsSystem = true,
            IsActiveLegacy = true
        };
        var mbrOsC = new PartitionSnapshot
        {
            DiskNumber = 0,
            PartitionNumber = 2,
            DriveLetter = "C",
            Label = "Windows",
            FileSystem = "NTFS",
            OffsetBytes = 501L * mb,
            SizeBytes = 930L * gb,
            FreeBytes = 210L * gb,
            Role = PartitionRole.WindowsOs,
            IsBoot = true
        };

        DiskSnapshot mbrHdd = win10Hdd.Environment.AllDisks[0] with
        {
            PartitionStyle = PartitionStyleType.Mbr,
            Partitions = new[] { sysReserved, mbrOsC }
        };

        SystemEnvironmentSnapshot legacyEnv = win10Hdd.Environment with
        {
            Firmware = FirmwareMode.LegacyBios,
            SecureBootEnabled = false,
            TargetOsPartitionNumber = 2,
            AllDisks = new[] { mbrHdd }
        };

        var evaluator = new ReadinessEvaluator(new PartitionSizingCalculator());
        ReadinessReport readiness = evaluator.Evaluate(legacyEnv, win10Hdd.SampleIso);

        Assert.Equal(0, readiness.BlockerCount);
        Assert.True(readiness.CanSimulateDryRun);
    }

    [Fact]
    public void ReadinessEvaluator_SmartMbrAndGptRules_HandlesWin11LegacyMbr_SinglePartitionMbr_And_Mismatches()
    {
        SimulationScenario baseWin11 = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        const long mb = ByteFormatting.OneMb;
        const long gb = ByteFormatting.OneGb;

        // 1. Single-partition Active C: MBR disk on Legacy BIOS targeting Windows 11 24H2 -> Allowed + Auto-Bypass + Format Cheat Sheet!
        var singleActiveC = new PartitionSnapshot
        {
            DiskNumber = 0,
            PartitionNumber = 1,
            DriveLetter = "C",
            Label = "Windows",
            FileSystem = "NTFS",
            OffsetBytes = 1L * mb,
            SizeBytes = 475L * gb,
            FreeBytes = 190L * gb,
            Role = PartitionRole.WindowsOs,
            IsBoot = true,
            IsSystem = true,
            IsActiveLegacy = true
        };

        DiskSnapshot singleMbrDisk = baseWin11.Environment.AllDisks[0] with
        {
            PartitionStyle = PartitionStyleType.Mbr,
            MbrPrimarySlotsUsed = 1,
            MbrSignature = 0x53574249u,
            Partitions = new[] { singleActiveC }
        };

        SystemEnvironmentSnapshot singleMbrWin11Env = baseWin11.Environment with
        {
            Firmware = FirmwareMode.LegacyBios,
            SecureBootEnabled = false,
            TargetOsPartitionNumber = 1,
            AllDisks = new[] { singleMbrDisk }
        };

        var evaluator = new ReadinessEvaluator(new PartitionSizingCalculator());
        ReadinessReport win11OnMbrReport = evaluator.Evaluate(singleMbrWin11Env, baseWin11.SampleIso);

        Assert.Equal(0, win11OnMbrReport.BlockerCount);
        Assert.True(win11OnMbrReport.CanSimulateDryRun);
        Assert.True(win11OnMbrReport.Compatibility?.RecommendedOptions.BypassWin11HardwareChecks);

        // Verify Cheat Sheet smartly tells single-partition MBR user to FORMAT (not Delete) C:!
        SetupPartitionGuide mbrGuide = new SetupGuideGenerator().Generate(singleMbrWin11Env, win11OnMbrReport.SizingPlan!);
        SetupPartitionRow targetRow = mbrGuide.Rows.First(r => r.Action == SetupPartitionAction.DeleteOrFormatTarget);
        Assert.Contains("FORMAT THIS ONE", targetRow.ActionBadgeText);

        // 2. UEFI Firmware + MBR Disk mismatch -> Blocker explaining EFI-requires-GPT Setup rule!
        SystemEnvironmentSnapshot uefiOnMbrEnv = singleMbrWin11Env with { Firmware = FirmwareMode.Uefi };
        ReadinessReport uefiMbrMismatch = evaluator.Evaluate(uefiOnMbrEnv, baseWin11.SampleIso);
        Assert.Contains(uefiMbrMismatch.Checks, c => c.Id == "disk-gpt" && c.Severity == CheckSeverity.Blocker && c.Title.Contains("UEFI + MBR Mismatch"));

        // 3. Legacy BIOS Firmware + GPT Disk mismatch -> Blocker explaining BIOS-refuses-GPT Setup rule!
        SystemEnvironmentSnapshot biosOnGptEnv = baseWin11.Environment with { Firmware = FirmwareMode.LegacyBios };
        ReadinessReport biosGptMismatch = evaluator.Evaluate(biosOnGptEnv, baseWin11.SampleIso);
        Assert.Contains(biosGptMismatch.Checks, c => c.Id == "firmware-mode" && c.Severity == CheckSeverity.Blocker && c.Title.Contains("Legacy BIOS Booted on GPT"));
    }
}
