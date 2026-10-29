using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Simulation;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class PartitionSizingCalculatorTests
{
    private readonly PartitionSizingCalculator _calculator = new();

    [Fact]
    public void Calculate_StandardWin11IsoWith5GbWim_ProducesValidFat32SplitPlan()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        PartitionSizingPlan plan = _calculator.Calculate(
            scenario.SampleIso,
            scenario.Environment.TargetOsPartition,
            scenario.Environment.ShrinkInfo);

        Assert.True(plan.FitsWithinFat32Limit);
        Assert.True(plan.FitsWithinShrinkLimit);
        Assert.Null(plan.BlockingReason);
        Assert.Equal(WimSplitStrategy.SplitWimToSwm, plan.SplitStrategy);
        Assert.Equal(2, plan.EstimatedSwmCount);
        Assert.True(plan.InstallerPartitionMb >= PartitionSizingPlan.MinInstallerPartitionMb);
        Assert.True(plan.InstallerPartitionMb <= PartitionSizingPlan.MaxNativeFat32PartitionMb);
        Assert.Equal(0L, plan.InstallerPartitionMb % 64L);
    }

    [Fact]
    public void Calculate_WhenUnmovableFilesRestrictShrink_BlocksSafely()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        var tightShrink = new ShrinkAnalysis
        {
            CurrentSizeBytes = 475L * ByteFormatting.OneGb,
            MinimumSupportedSizeBytes = 470L * ByteFormatting.OneGb, // Only 5 GB raw shrinkable due to unmovable tail
            VolumeFreeBytes = 120L * ByteFormatting.OneGb,
            OsSafetyReserveBytes = PartitionSizingCalculator.DefaultOsSafetyReserveBytes
        };

        PartitionSizingPlan plan = _calculator.Calculate(
            scenario.SampleIso,
            scenario.Environment.TargetOsPartition,
            tightShrink);

        Assert.False(plan.FitsWithinShrinkLimit);
        Assert.NotNull(plan.BlockingReason);
        Assert.Contains("can be shrunk", plan.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Calculate_WhenShrinkLeavesLessThan8GbFreeOnC_BlocksSafely()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        var lowFreeSpace = new ShrinkAnalysis
        {
            CurrentSizeBytes = 475L * ByteFormatting.OneGb,
            MinimumSupportedSizeBytes = 400L * ByteFormatting.OneGb,
            VolumeFreeBytes = 12L * ByteFormatting.OneGb, // Shrinking ~8.5 GB would leave < 8 GB free on C:
            OsSafetyReserveBytes = 8L * ByteFormatting.OneGb
        };

        PartitionSizingPlan plan = _calculator.Calculate(
            scenario.SampleIso,
            scenario.Environment.TargetOsPartition,
            lowFreeSpace);

        Assert.False(plan.FitsWithinShrinkLimit);
        Assert.NotNull(plan.BlockingReason);
        Assert.Contains("free space", plan.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Calculate_WhenOversizedIsoExceeds32GbFat32Ceiling_BlocksSafely()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        IsoInspectionResult hugeIso = scenario.SampleIso with
        {
            FileSizeBytes = 33L * ByteFormatting.OneGb
        };

        PartitionSizingPlan plan = _calculator.Calculate(
            hugeIso,
            scenario.Environment.TargetOsPartition,
            scenario.Environment.ShrinkInfo);

        Assert.False(plan.FitsWithinFat32Limit);
        Assert.NotNull(plan.BlockingReason);
        Assert.Contains("FAT32", plan.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }
}
