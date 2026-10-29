using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Simulation;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class CleanupServiceTests
{
    [Fact]
    public async Task InspectAsync_WhenAdjacentWinInstallWithValidManifest_AllowsDirectOsExtension()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        CleanupService service = CreateCleanupService(scenario.Environment);

        CleanupInspectionResult inspection = await service.InspectAsync(
            scenario.Environment,
            scenario.CleanupManifest);

        Assert.Equal(CleanupSafetyState.ReadyToClean, inspection.State);
        Assert.NotNull(inspection.Adjacency);
        Assert.True(inspection.Adjacency!.CanExtendOsPartitionDirectly);
    }

    [Fact]
    public async Task InspectAsync_WhenWinReSitsBetweenOsAndWinInstall_DisablesAutoExtensionHonestly()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallRecoveryBlockedExtendScenario();
        CleanupService service = CreateCleanupService(scenario.Environment);

        CleanupInspectionResult inspection = await service.InspectAsync(
            scenario.Environment,
            scenario.CleanupManifest);

        Assert.Equal(CleanupSafetyState.ReadyToClean, inspection.State);
        Assert.NotNull(inspection.Adjacency);
        Assert.False(inspection.Adjacency!.CanExtendOsPartitionDirectly);
        Assert.Contains("Recovery", inspection.Adjacency.InterveningPartitionsSummary);
    }

    [Fact]
    public async Task InspectAsync_WhenLabelMatchesButManifestMissing_BlocksAutomaticDeletion()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        CleanupService service = CreateCleanupService(scenario.Environment);

        CleanupInspectionResult inspection = await service.InspectAsync(
            scenario.Environment,
            simulatedManifest: null);

        Assert.Equal(CleanupSafetyState.BlockedAmbiguousPartition, inspection.State);
        Assert.False(inspection.CanExecuteCleanup);
    }

    [Fact]
    public async Task InspectAsync_UefiOnlyManifest_DoesNotInferOldSystemBcdEntries()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        SystemEnvironmentSnapshot env = scenario.Environment with { IsSimulated = false };
        InstallerManifest manifest = scenario.CleanupManifest! with
        {
            BootFirmwareMode = FirmwareMode.Uefi,
            BcdOsLoaderGuid = null,
            BcdSdiDeviceGuid = null,
            BcdFirmwareBootGuid = "UEFI:Boot0010"
        };
        var boot = new NoOpBcdService
        {
            State = new ActiveBcdState(false, "partition=C:", "partition=C:", "{old-loader}", "{old-sdi}", "UEFI:Boot0010")
        };
        var service = new CleanupService(new NoOpDiskInspector(env), new NoOpPartitionService(), boot, new SafeRedactingLogger());
        CleanupInspectionResult result = await service.InspectAsync(env, manifest);
        Assert.Null(result.OrphanedBcdLoaderGuid);
        Assert.Null(result.OrphanedBcdSdiGuid);
        Assert.Equal("UEFI:Boot0010", result.OrphanedBcdFirmwareGuid);
    }

    [Fact]
    public async Task InspectAsync_LegacyManifest_DoesNotInferUnrelatedFirmwareEntry()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        SystemEnvironmentSnapshot env = scenario.Environment with { IsSimulated = false };
        InstallerManifest manifest = scenario.CleanupManifest! with
        {
            BootFirmwareMode = FirmwareMode.LegacyBios,
            BcdOsLoaderGuid = "{11111111-1111-1111-1111-111111111111}",
            BcdSdiDeviceGuid = "{22222222-2222-2222-2222-222222222222}",
            BcdFirmwareBootGuid = null
        };
        var boot = new NoOpBcdService { State = new ActiveBcdState(false, "partition=C:", "partition=C:", null, null, "UEFI:Boot0030") };
        var service = new CleanupService(new NoOpDiskInspector(env), new NoOpPartitionService(), boot, new SafeRedactingLogger());
        CleanupInspectionResult result = await service.InspectAsync(env, manifest);
        Assert.Equal(CleanupSafetyState.ReadyToClean, result.State);
        Assert.Null(result.OrphanedBcdFirmwareGuid);
        Assert.Equal(manifest.BcdOsLoaderGuid, result.OrphanedBcdLoaderGuid);
    }

    [Theory]
    [InlineData(FirmwareMode.Uefi)]
    [InlineData(FirmwareMode.LegacyBios)]
    [InlineData(FirmwareMode.Unknown)]
    public async Task InspectAsync_MissingRecordedBootIdentity_BlocksPartitionDeletion(FirmwareMode mode)
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        InstallerManifest manifest = scenario.CleanupManifest! with
        {
            BootFirmwareMode = mode, BcdOsLoaderGuid = null, BcdSdiDeviceGuid = null, BcdFirmwareBootGuid = null
        };
        CleanupInspectionResult result = await CreateCleanupService(scenario.Environment).InspectAsync(scenario.Environment, manifest);
        Assert.Equal(CleanupSafetyState.BlockedAmbiguousPartition, result.State);
        Assert.False(result.CanExecuteCleanup);
    }

    [Theory]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("UEFI:Boot0001")]
    [InlineData("UEFI:BootZZZZ")]
    public async Task InspectAsync_UefiManifestWithInvalidFirmwareId_BlocksWithoutFallingBackToBcd(string id)
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        InstallerManifest manifest = scenario.CleanupManifest! with { BcdFirmwareBootGuid = id };
        CleanupInspectionResult result = await CreateCleanupService(scenario.Environment).InspectAsync(scenario.Environment, manifest);
        Assert.Equal(CleanupSafetyState.BlockedAmbiguousPartition, result.State);
        Assert.False(result.CanExecuteCleanup);
        Assert.Null(result.OrphanedBcdLoaderGuid);
    }

    [Fact]
    public async Task InspectAsync_NewManifestWithMissingMode_BlocksButOldManifestRemainsCompatible()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        InstallerManifest incomplete = scenario.CleanupManifest! with { BootFirmwareMode = null, BcdFirmwareBootGuid = null };
        var service = CreateCleanupService(scenario.Environment);
        CleanupInspectionResult result = await service.InspectAsync(scenario.Environment, incomplete);
        Assert.Equal(CleanupSafetyState.BlockedAmbiguousPartition, result.State);

        InstallerManifest old = incomplete with { SchemaVersion = "1.2", BcdFirmwareBootGuid = "UEFI:Boot0010" };
        result = await service.InspectAsync(scenario.Environment, old);
        Assert.Equal(CleanupSafetyState.ReadyToClean, result.State);
    }

    private static CleanupService CreateCleanupService(SystemEnvironmentSnapshot env) =>
        new(
            new NoOpDiskInspector(env),
            new NoOpPartitionService(),
            new NoOpBcdService(),
            new SafeRedactingLogger());

    private sealed class NoOpDiskInspector(SystemEnvironmentSnapshot env) : IDiskInspector
    {
        public Task<SystemEnvironmentSnapshot> InspectEnvironmentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(env);
    }

    private sealed class NoOpPartitionService : IPartitionService
    {
        public Task SuspendBitLockerForNextRebootAsync(string volumeLetter, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OptimizeOsVolumeBeforeShrinkAsync(string volumeLetter, StorageDriveKind driveKind, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CreatedInstallerPartitionInfo> ShrinkOsAndCreateFat32InstallerPartitionAsync(int diskNumber, int osPartitionNumber, string expectedOsPartitionGuid, long newOsPartitionSizeBytes, long installerPartitionSizeBytes, string volumeLabel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WriteInstallerManifestAsync(string installerDriveLetter, InstallerManifest manifest, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteInstallerPartitionAsync(int diskNumber, int installerPartitionNumber, string expectedPartitionGuid, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ExtendOsPartitionIntoReclaimedSpaceAsync(int diskNumber, int osPartitionNumber, CancellationToken cancellationToken = default, long expectedOsPartitionOffsetBytes = 0) => Task.CompletedTask;
    }

    private sealed class NoOpBcdService : IBcdBootService
    {
        public ActiveBcdState State { get; init; } = new(false, "partition=C:", "partition=C:", null, null, null);
        public Task<BcdProvisionResult> ConfigureOneTimeWinPeBootAsync(string installerDriveLetter, string backupDirectory, FirmwareMode firmwareMode = FirmwareMode.Uefi, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RollbackBcdEntriesAsync(string? osLoaderGuid, string? sdiDeviceGuid, CancellationToken cancellationToken = default, string? firmwareBootGuid = null) => Task.CompletedTask;
        public Task<ActiveBcdState> InspectActiveBcdAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task RebootSystemAsync(int delaySeconds = 5, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
