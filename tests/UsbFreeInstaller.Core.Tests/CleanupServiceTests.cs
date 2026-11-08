using System.Text.Json;
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
    public async Task InspectAsync_VerifiedManifest_AllowsCleanupWithoutAnInstallerDriveLetter()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        DiskSnapshot disk = scenario.Environment.TargetDisk!;
        SystemEnvironmentSnapshot env = scenario.Environment with
        {
            AllDisks = new[] { disk with
            {
                Partitions = disk.Partitions.Select(p => p.Role == PartitionRole.TemporaryInstaller
                    ? p with { DriveLetter = null } : p).ToArray()
            } }
        };
        CleanupInspectionResult result = await CreateCleanupService(env).InspectAsync(env, scenario.CleanupManifest);
        Assert.Equal(CleanupSafetyState.ReadyToClean, result.State);
        Assert.Null(result.CandidatePartition?.DriveLetter);
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
    public async Task BootOnlyRecovery_WithRolledBackPartition_RemovesOnlyInspectedBootEntries()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        DiskSnapshot disk = scenario.Environment.TargetDisk!;
        SystemEnvironmentSnapshot env = scenario.Environment with
        {
            IsSimulated = false,
            AllDisks = new[] { disk with
            {
                Partitions = disk.Partitions.Where(p => p.Role != PartitionRole.TemporaryInstaller).ToArray()
            } }
        };
        const string loader = "{11111111-1111-1111-1111-111111111111}";
        const string sdi = "{22222222-2222-2222-2222-222222222222}";
        var boot = new NoOpBcdService
        {
            State = new ActiveBcdState(false, "partition=C:", "partition=C:", loader, sdi)
        };
        var partitions = new NoOpPartitionService();
        var service = new CleanupService(new NoOpDiskInspector(env), partitions, boot, new SafeRedactingLogger());
        CleanupInspectionResult inspection = await service.InspectAsync();
        Assert.Equal(CleanupSafetyState.ReadyToClean, inspection.State);
        Assert.Null(inspection.CandidatePartition);
        Assert.Equal(loader, inspection.OrphanedBcdLoaderGuid);
        Assert.Contains(inspection.SafetyChecks, c => c.Id == "cleanup-boot-only");

        CleanupExecutionResult dryRun = await service.ExecuteCleanupAsync(inspection, true, ExecutionMode.DryRun);
        Assert.True(dryRun.Succeeded);
        Assert.False(dryRun.PartitionDeleted);
        Assert.False(dryRun.OsPartitionExtended);
        Assert.Contains("boot entries only", dryRun.Summary);
        Assert.Equal(0, boot.RollbackCalls);

        CleanupExecutionResult result = await service.ExecuteCleanupAsync(inspection, true, ExecutionMode.Live);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(result.BcdEntriesRemoved);
        Assert.False(result.PartitionDeleted);
        Assert.False(result.OsPartitionExtended);
        Assert.Contains("No partition was deleted", result.Summary);
        Assert.Equal(1, boot.RollbackCalls);
        Assert.Equal(0, partitions.DeleteCalls);
        Assert.Equal(0, partitions.ExtendCalls);
    }

    [Fact]
    public async Task BootOnlyRecovery_LegacyUefiSlotWithoutPartition_IsIdentifiedWithoutDiskWrites()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        DiskSnapshot disk = scenario.Environment.TargetDisk!;
        SystemEnvironmentSnapshot env = scenario.Environment with
        {
            IsSimulated = false,
            AllDisks = new[] { disk with
            {
                Partitions = disk.Partitions.Where(p => p.Role != PartitionRole.TemporaryInstaller).ToArray()
            } }
        };
        var boot = new NoOpBcdService
        {
            State = new ActiveBcdState(false, "partition=C:", "partition=C:", null, null, "UEFI:Boot0010")
        };
        var partitions = new NoOpPartitionService();
        var service = new CleanupService(new NoOpDiskInspector(env), partitions, boot, new SafeRedactingLogger());
        CleanupInspectionResult inspection = await service.InspectAsync();
        Assert.Equal(CleanupSafetyState.ReadyToClean, inspection.State);
        Assert.Null(inspection.CandidatePartition);
        Assert.Equal("UEFI:Boot0010", inspection.OrphanedBcdFirmwareGuid);

        CleanupExecutionResult result = await service.ExecuteCleanupAsync(inspection, true, ExecutionMode.Live);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(1, boot.RollbackCalls);
        Assert.Equal(0, partitions.DeleteCalls);
        Assert.Equal(0, partitions.ExtendCalls);
    }

    [Fact]
    public async Task BootOnlyRecovery_IfBootIdentityChangesAfterInspection_RefusesStaleCleanup()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        DiskSnapshot disk = scenario.Environment.TargetDisk!;
        SystemEnvironmentSnapshot env = scenario.Environment with
        {
            IsSimulated = false,
            AllDisks = new[] { disk with
            {
                Partitions = disk.Partitions.Where(p => p.Role != PartitionRole.TemporaryInstaller).ToArray()
            } }
        };
        var boot = new NoOpBcdService
        {
            State = new ActiveBcdState(false, "partition=C:", "partition=C:",
                "{11111111-1111-1111-1111-111111111111}", null)
        };
        var partitions = new NoOpPartitionService();
        var service = new CleanupService(new NoOpDiskInspector(env), partitions, boot, new SafeRedactingLogger());
        CleanupInspectionResult inspected = await service.InspectAsync();
        boot.State = boot.State with { ExistingUsbFreeLoaderGuid = "{33333333-3333-3333-3333-333333333333}" };

        CleanupExecutionResult result = await service.ExecuteCleanupAsync(inspected, true, ExecutionMode.Live);
        Assert.False(result.Succeeded);
        Assert.Contains("Nothing was removed", result.Summary);
        Assert.Equal(0, boot.RollbackCalls);
        Assert.Equal(0, partitions.DeleteCalls);
        Assert.Equal(0, partitions.ExtendCalls);
    }

    [Fact]
    public async Task Cleanup_IfSystemBcdCannotBeInspected_FailsClosedEvenWithoutInstallerPartition()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        SystemEnvironmentSnapshot env = scenario.Environment with { IsSimulated = false };
        var boot = new NoOpBcdService
        {
            State = new ActiveBcdState(false, string.Empty, string.Empty, null, null,
                InspectionSucceeded: false)
        };
        var service = new CleanupService(new NoOpDiskInspector(env), new NoOpPartitionService(), boot, new SafeRedactingLogger());
        CleanupInspectionResult result = await service.InspectAsync();
        Assert.Equal(CleanupSafetyState.BlockedActiveDependency, result.State);
        Assert.False(result.CanExecuteCleanup);
        Assert.Contains(result.SafetyChecks, c => c.Id == "cleanup-bcd-inspection");
    }

    [Fact]
    public async Task InspectAsync_UefiOnlyManifest_DoesNotInferOldSystemBcdEntries()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        SystemEnvironmentSnapshot env = scenario.Environment with { IsSimulated = false };
        InstallerManifest manifest = scenario.CleanupManifest! with
        {
            SchemaVersion = "1.3",
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
    public async Task InspectAsync_NewUefiBcdManifest_UsesRecordedBcdIds_NotAnUnrelatedFirmwareSlot()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        SystemEnvironmentSnapshot env = scenario.Environment with { IsSimulated = false };
        InstallerManifest manifest = scenario.CleanupManifest!;
        var boot = new NoOpBcdService
        {
            State = new ActiveBcdState(false, "partition=C:", "partition=C:", "{unrelated}", "{unrelated}", "UEFI:Boot0030")
        };
        var service = new CleanupService(new NoOpDiskInspector(env), new NoOpPartitionService(), boot, new SafeRedactingLogger());
        CleanupInspectionResult result = await service.InspectAsync(env, manifest);

        Assert.Equal(CleanupSafetyState.ReadyToClean, result.State);
        Assert.Equal(manifest.BcdOsLoaderGuid, result.OrphanedBcdLoaderGuid);
        Assert.Equal(manifest.BcdSdiDeviceGuid, result.OrphanedBcdSdiGuid);
        Assert.Null(result.OrphanedBcdFirmwareGuid);
    }

    [Fact]
    public async Task InspectAsync_UefiManifestWithBothBcdAndFirmwareIdentities_BlocksDeletion()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        InstallerManifest mixed = scenario.CleanupManifest! with { BcdFirmwareBootGuid = "UEFI:Boot0010" };
        CleanupInspectionResult result = await CreateCleanupService(scenario.Environment).InspectAsync(scenario.Environment, mixed);
        Assert.Equal(CleanupSafetyState.BlockedAmbiguousPartition, result.State);
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
        InstallerManifest manifest = scenario.CleanupManifest! with
        {
            SchemaVersion = "1.3", BcdOsLoaderGuid = null, BcdSdiDeviceGuid = null, BcdFirmwareBootGuid = id
        };
        CleanupInspectionResult result = await CreateCleanupService(scenario.Environment).InspectAsync(scenario.Environment, manifest);
        Assert.Equal(CleanupSafetyState.BlockedAmbiguousPartition, result.State);
        Assert.False(result.CanExecuteCleanup);
        Assert.Null(result.OrphanedBcdLoaderGuid);
    }

    [Fact]
    public async Task InspectAsync_NewManifestWithMissingMode_BlocksButOldManifestRemainsCompatible()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        InstallerManifest incomplete = scenario.CleanupManifest! with
        {
            BootFirmwareMode = null, BcdOsLoaderGuid = null, BcdSdiDeviceGuid = null, BcdFirmwareBootGuid = null
        };
        var service = CreateCleanupService(scenario.Environment);
        CleanupInspectionResult result = await service.InspectAsync(scenario.Environment, incomplete);
        Assert.Equal(CleanupSafetyState.BlockedAmbiguousPartition, result.State);

        InstallerManifest old = incomplete with { SchemaVersion = "1.2", BcdFirmwareBootGuid = "UEFI:Boot0010" };
        result = await service.InspectAsync(scenario.Environment, old);
        Assert.Equal(CleanupSafetyState.ReadyToClean, result.State);
    }

    [Fact]
    public void ReadManifestFromRoot_WorksWithoutADriveLetter_AndRejectsOversizedRecord()
    {
        string root = Path.Combine(Path.GetTempPath(), $"swbi-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            InstallerManifest manifest = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario().CleanupManifest!;
            string file = Path.Combine(root, SwbiBrand.ManifestFileName);
            File.WriteAllText(file, JsonSerializer.Serialize(manifest));
            Assert.Equal(manifest.InstallerPartitionGuid,
                CleanupService.ReadManifestFromRoot(root)?.InstallerPartitionGuid);
            File.WriteAllText(file, new string(' ', 1024 * 1024 + 1));
            Assert.Throws<InvalidDataException>(() => CleanupService.ReadManifestFromRoot(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("delete")]
    [InlineData("extend")]
    public async Task ExecuteCleanupAsync_ReportsExactlyWhichStagesCompletedOnFailure(string failAt)
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreatePostInstallAdjacentCleanupScenario();
        var partitions = new NoOpPartitionService { FailAt = failAt };
        var boot = new NoOpBcdService { FailRollback = failAt == "boot" };
        var service = new CleanupService(new NoOpDiskInspector(scenario.Environment), partitions, boot, new SafeRedactingLogger());
        CleanupInspectionResult inspection = await service.InspectAsync(scenario.Environment, scenario.CleanupManifest);
        Assert.Equal(CleanupSafetyState.ReadyToClean, inspection.State);

        CleanupExecutionResult result = await service.ExecuteCleanupAsync(inspection, true, ExecutionMode.Live);
        Assert.False(result.Succeeded);
        Assert.Equal(failAt != "boot", result.BcdEntriesRemoved);
        Assert.Equal(failAt == "extend", result.PartitionDeleted);
        Assert.False(result.OsPartitionExtended);
        Assert.Equal(failAt == "extend", result.Summary.Contains("WAS deleted", StringComparison.Ordinal));
        if (failAt == "delete") Assert.Contains("could not be verified", result.Summary);
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
        public string? FailAt { get; init; }
        public int DeleteCalls { get; private set; }
        public int ExtendCalls { get; private set; }
        public Task SuspendBitLockerForNextRebootAsync(string volumeLetter, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OptimizeOsVolumeBeforeShrinkAsync(string volumeLetter, StorageDriveKind driveKind, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CreatedInstallerPartitionInfo> ShrinkOsAndCreateFat32InstallerPartitionAsync(int diskNumber, int osPartitionNumber, string expectedOsPartitionGuid, long newOsPartitionSizeBytes, long installerPartitionSizeBytes, string volumeLabel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WriteInstallerManifestAsync(string installerDriveLetter, InstallerManifest manifest, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteInstallerPartitionAsync(int diskNumber, int installerPartitionNumber, string expectedPartitionGuid, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return FailAt == "delete" ? Task.FromException(new IOException("delete failed")) : Task.CompletedTask;
        }
        public Task ExtendOsPartitionIntoReclaimedSpaceAsync(int diskNumber, int osPartitionNumber, CancellationToken cancellationToken = default, long expectedOsPartitionOffsetBytes = 0)
        {
            ExtendCalls++;
            return FailAt == "extend" ? Task.FromException(new IOException("extend failed")) : Task.CompletedTask;
        }
    }

    private sealed class NoOpBcdService : IBcdBootService
    {
        public ActiveBcdState State { get; set; } = new(false, "partition=C:", "partition=C:", null, null, null);
        public bool FailRollback { get; init; }
        public int RollbackCalls { get; private set; }
        public Task<BcdProvisionResult> ConfigureOneTimeWinPeBootAsync(string installerDriveLetter, string backupDirectory, FirmwareMode firmwareMode = FirmwareMode.Uefi, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RollbackBcdEntriesAsync(string? osLoaderGuid, string? sdiDeviceGuid, CancellationToken cancellationToken = default, string? firmwareBootGuid = null)
        {
            RollbackCalls++;
            return FailRollback ? Task.FromException(new IOException("boot cleanup failed")) : Task.CompletedTask;
        }
        public Task<ActiveBcdState> InspectActiveBcdAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task RebootSystemAsync(int delaySeconds = 5, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
