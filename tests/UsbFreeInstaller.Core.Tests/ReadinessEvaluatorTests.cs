using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Simulation;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class ReadinessEvaluatorTests
{
    private readonly ReadinessEvaluator _evaluator = new(new PartitionSizingCalculator());

    [Fact]
    public void Evaluate_HealthySingleDriveScenario_HasZeroBlockers()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        ReadinessReport report = _evaluator.Evaluate(scenario.Environment, scenario.SampleIso);

        Assert.Equal(0, report.BlockerCount);
        Assert.True(report.CanSimulateDryRun);
        Assert.False(report.RequiresBitLockerRecoveryConfirmation);
    }

    [Fact]
    public void Evaluate_LegacyBiosMbrFull4SlotsScenario_ProducesMbrSlotExhaustionBlocker()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateLegacyBiosUnsupportedScenario();
        ReadinessReport report = _evaluator.Evaluate(scenario.Environment, scenario.SampleIso);

        Assert.False(report.CanSimulateDryRun);
        Assert.Contains(report.Checks, c => c.Id == "disk-gpt" && c.Severity == CheckSeverity.Blocker);
        Assert.Contains(report.Checks, c => c.Id == "firmware-mode" && c.Severity == CheckSeverity.Blocker);
    }

    [Fact]
    public void Evaluate_BitLockerActive_RequiresRecoveryKeyConfirmation()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateBitLockerLaptopScenario();
        ReadinessReport report = _evaluator.Evaluate(scenario.Environment, scenario.SampleIso);

        Assert.True(report.RequiresBitLockerRecoveryConfirmation);
        Assert.Contains(report.Checks, c => c.Id == "bitlocker-status" && c.Severity == CheckSeverity.Warning);
        Assert.Contains(report.Checks, c => c.Id == "power-ac" && c.Severity == CheckSeverity.Warning);
    }

    [Fact]
    public void Evaluate_BitLockerConversionInFlight_BlocksImmediately()
    {
        SimulationScenario baseScenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        SystemEnvironmentSnapshot convertingEnv = baseScenario.Environment with
        {
            BitLocker = new BitLockerStatusInfo
            {
                VolumeLetter = "C",
                ProtectionState = BitLockerProtectionState.Off,
                ConversionState = BitLockerConversionState.EncryptionInProgress,
                HasRecoveryPasswordProtector = true
            }
        };

        ReadinessReport report = _evaluator.Evaluate(convertingEnv, baseScenario.SampleIso);
        Assert.Contains(report.Checks, c => c.Id == "bitlocker-status" && c.Severity == CheckSeverity.Blocker);
    }

    [Fact]
    public void Evaluate_UnhealthyDisk_BlocksImmediately()
    {
        SimulationScenario baseScenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        DiskSnapshot failingDisk = baseScenario.Environment.AllDisks[0] with
        {
            Health = DiskHealthState.Unhealthy,
            OperationalStatus = "Predictive Failure"
        };
        SystemEnvironmentSnapshot env = baseScenario.Environment with { AllDisks = new[] { failingDisk } };

        ReadinessReport report = _evaluator.Evaluate(env, baseScenario.SampleIso);
        Assert.Contains(report.Checks, c => c.Id == "disk-health" && c.Severity == CheckSeverity.Blocker);
    }

    [Fact]
    public void Evaluate_IsoArchitectureMismatch_BlocksImmediately()
    {
        SimulationScenario baseScenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        IsoInspectionResult armIso = baseScenario.SampleIso with { Architecture = "arm64" };

        ReadinessReport report = _evaluator.Evaluate(baseScenario.Environment, armIso);
        Assert.Contains(report.Checks, c => c.Id == "iso-arch" && c.Severity == CheckSeverity.Blocker);
    }
}
