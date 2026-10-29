using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Native;
using UsbFreeInstaller.Core.Services.Simulation;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class PreparationOrchestratorTests
{
    [Fact]
    public async Task ExecuteAsync_InDryRunMode_NeverCallsMutatingServices()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        var evaluator = new ReadinessEvaluator(new PartitionSizingCalculator());
        ReadinessReport report = evaluator.Evaluate(scenario.Environment, scenario.SampleIso);

        var spyPartitions = new SpyPartitionService();
        var spyDeploy = new SpyDeploymentService();
        var spyBcd = new SpyBcdService();

        var orchestrator = new PreparationOrchestrator(
            new StubDiskInspector(scenario.Environment),
            spyPartitions,
            spyDeploy,
            spyBcd,
            evaluator,
            new SetupGuideGenerator(),
            new SafeRedactingLogger());

        var confirmations = new UserSafetyConfirmations
        {
            HasVerifiedExternalBackup = true,
            UnderstandsCleanInstallWipesTargetPartition = true,
            UnderstandsNeverDeleteInstallerPartition = true
        };

        PreparationResult result = await orchestrator.ExecuteAsync(report, confirmations, ExecutionMode.DryRun);

        Assert.True(result.Succeeded);
        Assert.Equal(ExecutionMode.DryRun, result.Mode);
        Assert.NotNull(result.SetupGuide);
        Assert.Equal(0, spyPartitions.ShrinkCreateCalls);
        Assert.Equal(0, spyDeploy.DeployCalls);
        Assert.Equal(0, spyBcd.ConfigureCalls);
    }

    [Fact]
    public async Task ExecuteAsync_InLiveMode_WhenDeploymentFails_RollsBackPartitionAndExtendsOs()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        SystemEnvironmentSnapshot realLikeEnv = scenario.Environment with { IsSimulated = false };

        var evaluator = new ReadinessEvaluator(new PartitionSizingCalculator());
        ReadinessReport report = evaluator.Evaluate(realLikeEnv, scenario.SampleIso);

        var spyPartitions = new SpyPartitionService();
        var failingDeploy = new SpyDeploymentService { ThrowOnDeploy = true };
        var spyBcd = new SpyBcdService();

        var orchestrator = new PreparationOrchestrator(
            new StubDiskInspector(realLikeEnv),
            spyPartitions,
            failingDeploy,
            spyBcd,
            evaluator,
            new SetupGuideGenerator(),
            new SafeRedactingLogger());

        var confirmations = new UserSafetyConfirmations
        {
            HasVerifiedExternalBackup = true,
            UnderstandsCleanInstallWipesTargetPartition = true,
            UnderstandsNeverDeleteInstallerPartition = true
        };

        PreparationResult result = await orchestrator.ExecuteAsync(report, confirmations, ExecutionMode.Live);

        Assert.False(result.Succeeded);
        Assert.Equal(1, spyPartitions.ShrinkCreateCalls);
        Assert.Equal(1, spyPartitions.DeletePartitionCalls);
        Assert.Equal(1, spyPartitions.ExtendOsCalls);
        Assert.Equal(0, spyBcd.ConfigureCalls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDowngradingWithInPlaceKeepAppsOption_RejectsInvalidModeSafely()
    {
        SimulationScenario downgradeScenario = SimulatedEnvironmentProvider.CreateSataHddDowngradeWin11ToWin10Scenario();
        var evaluator = new ReadinessEvaluator(new PartitionSizingCalculator());
        ReadinessReport report = evaluator.Evaluate(downgradeScenario.Environment, downgradeScenario.SampleIso);

        var orchestrator = new PreparationOrchestrator(
            new StubDiskInspector(downgradeScenario.Environment),
            new SpyPartitionService(),
            new SpyDeploymentService(),
            new SpyBcdService(),
            evaluator,
            new SetupGuideGenerator(),
            new SafeRedactingLogger());

        var confirmations = new UserSafetyConfirmations
        {
            HasVerifiedExternalBackup = true,
            UnderstandsCleanInstallWipesTargetPartition = true,
            UnderstandsNeverDeleteInstallerPartition = true
        };

        var invalidOptions = new SmartTailoringOptions
        {
            WorkflowChoice = InstallWorkflowChoice.InPlaceUpgradeKeepAppsAndFiles
        };

        PreparationResult result = await orchestrator.ExecuteAsync(
            report,
            confirmations,
            ExecutionMode.DryRun,
            tailoringOptions: invalidOptions);

        Assert.False(result.Succeeded);
        Assert.Contains("downgrading", result.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFirmwareCleanupIsUncertain_KeepsInstallerPartition()
    {
        var scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        var env = scenario.Environment with { IsSimulated = false };
        var evaluator = new ReadinessEvaluator(new PartitionSizingCalculator());
        var partitions = new SpyPartitionService();
        var boot = new SpyBcdService { ThrowOnConfigure = true };
        var orchestrator = new PreparationOrchestrator(new StubDiskInspector(env), partitions,
            new SpyDeploymentService(), boot, evaluator, new SetupGuideGenerator(), new SafeRedactingLogger());
        var confirmations = new UserSafetyConfirmations
        {
            HasVerifiedExternalBackup = true,
            UnderstandsCleanInstallWipesTargetPartition = true,
            UnderstandsNeverDeleteInstallerPartition = true
        };

        var result = await orchestrator.ExecuteAsync(evaluator.Evaluate(env, scenario.SampleIso), confirmations, ExecutionMode.Live);
        Assert.False(result.Succeeded);
        Assert.Equal(0, partitions.DeletePartitionCalls);
        Assert.Equal(0, partitions.ExtendOsCalls);
        Assert.Contains(result.PlannedOrExecutedActions, item => item.Contains("Kept the installer partition"));
        Assert.DoesNotContain("rolled back safely", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBootRollbackFails_KeepsInstallerPartition()
    {
        var scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        var env = scenario.Environment with { IsSimulated = false };
        var evaluator = new ReadinessEvaluator(new PartitionSizingCalculator());
        var partitions = new SpyPartitionService { ThrowOnManifest = true };
        var boot = new SpyBcdService { ThrowOnRollback = true };
        var orchestrator = new PreparationOrchestrator(new StubDiskInspector(env), partitions,
            new SpyDeploymentService(), boot, evaluator, new SetupGuideGenerator(), new SafeRedactingLogger());
        var confirmations = new UserSafetyConfirmations
        {
            HasVerifiedExternalBackup = true,
            UnderstandsCleanInstallWipesTargetPartition = true,
            UnderstandsNeverDeleteInstallerPartition = true
        };

        var result = await orchestrator.ExecuteAsync(evaluator.Evaluate(env, scenario.SampleIso), confirmations, ExecutionMode.Live);
        Assert.False(result.Succeeded);
        Assert.Equal(0, partitions.DeletePartitionCalls);
        Assert.Equal(0, partitions.ExtendOsCalls);
        Assert.Contains(result.PlannedOrExecutedActions, item => item.Contains("boot-entry cleanup failed"));
    }

    private sealed class StubDiskInspector(SystemEnvironmentSnapshot snapshot) : IDiskInspector
    {
        public Task<SystemEnvironmentSnapshot> InspectEnvironmentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class SpyPartitionService : IPartitionService
    {
        public int ShrinkCreateCalls { get; private set; }
        public int DeletePartitionCalls { get; private set; }
        public int ExtendOsCalls { get; private set; }
        public bool ThrowOnManifest { get; init; }

        public Task SuspendBitLockerForNextRebootAsync(string volumeLetter, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task OptimizeOsVolumeBeforeShrinkAsync(string volumeLetter, StorageDriveKind driveKind, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<CreatedInstallerPartitionInfo> ShrinkOsAndCreateFat32InstallerPartitionAsync(
            int diskNumber,
            int osPartitionNumber,
            string expectedOsPartitionGuid,
            long newOsPartitionSizeBytes,
            long installerPartitionSizeBytes,
            string volumeLabel,
            CancellationToken cancellationToken = default)
        {
            ShrinkCreateCalls++;
            return Task.FromResult(new CreatedInstallerPartitionInfo(
                diskNumber,
                osPartitionNumber + 1,
                "{ABCD1234-0000-4000-8000-000000000001}",
                "W",
                0L,
                installerPartitionSizeBytes));
        }

        public Task WriteInstallerManifestAsync(string installerDriveLetter, InstallerManifest manifest, CancellationToken cancellationToken = default)
        {
            if (ThrowOnManifest) throw new IOException("Simulated manifest failure.");
            return Task.CompletedTask;
        }

        public Task DeleteInstallerPartitionAsync(int diskNumber, int installerPartitionNumber, string expectedPartitionGuid, CancellationToken cancellationToken = default)
        {
            DeletePartitionCalls++;
            return Task.CompletedTask;
        }

        public Task ExtendOsPartitionIntoReclaimedSpaceAsync(int diskNumber, int osPartitionNumber, CancellationToken cancellationToken = default, long expectedOsPartitionOffsetBytes = 0)
        {
            ExtendOsCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class SpyDeploymentService : IIsoDeploymentService
    {
        public bool ThrowOnDeploy { get; init; }
        public int DeployCalls { get; private set; }

        public Task DeployIsoToInstallerPartitionAsync(
            IsoInspectionResult iso,
            string targetDriveLetter,
            int copyBufferSizeBytes = 1024 * 1024,
            bool writeEditionUnlockEiCfg = true,
            IProgress<DeploymentProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool bypassWin11HardwareChecks = false)
        {
            DeployCalls++;
            if (ThrowOnDeploy)
            {
                throw new IOException("Simulated ISO extraction failure.");
            }

            return Task.CompletedTask;
        }

        public Task VerifyDeployedBootAndInstallFilesAsync(
            IsoInspectionResult iso,
            string targetDriveLetter,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SpyBcdService : IBcdBootService
    {
        public int ConfigureCalls { get; private set; }
        public bool ThrowOnConfigure { get; init; }
        public bool ThrowOnRollback { get; init; }

        public Task<BcdProvisionResult> ConfigureOneTimeWinPeBootAsync(
            string installerDriveLetter,
            string backupDirectory,
            FirmwareMode firmwareMode = FirmwareMode.Uefi,
            CancellationToken cancellationToken = default)
        {
            ConfigureCalls++;
            if (ThrowOnConfigure) throw new FirmwareBootProvisionException("Simulated firmware cleanup failure.", true);
            return Task.FromResult(new BcdProvisionResult(
                "backup.bcd",
                "{11111111-1111-1111-1111-111111111111}",
                "{22222222-2222-2222-2222-222222222222}",
                "{33333333-3333-3333-3333-333333333333}"));
        }

        public Task RollbackBcdEntriesAsync(
            string? osLoaderGuid,
            string? sdiDeviceGuid,
            CancellationToken cancellationToken = default,
            string? firmwareBootGuid = null)
        {
            if (ThrowOnRollback) throw new IOException("Simulated boot rollback failure.");
            return Task.CompletedTask;
        }

        public Task<ActiveBcdState> InspectActiveBcdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActiveBcdState(false, "partition=C:", "partition=C:", null, null, null));

        public Task RebootSystemAsync(int delaySeconds = 5, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
