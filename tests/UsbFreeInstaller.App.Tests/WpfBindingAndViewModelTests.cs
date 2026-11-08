using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using UsbFreeInstaller.App.Infrastructure;
using UsbFreeInstaller.App.ViewModels;
using UsbFreeInstaller.App.Views;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Simulation;
using Xunit;

namespace UsbFreeInstaller.App.Tests;

public sealed class WpfBindingAndViewModelTests
{
    private static readonly object AppInitLock = new();

    [Fact]
    public void MainWindowXaml_StaticAudit_EnforcesSafeProgressBarAndDataContextVisibilityRules()
    {
        string repoRoot = FindRepoRoot();
        string xamlPath = Path.Combine(repoRoot, "src", "UsbFreeInstaller.App", "Views", "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"Expected MainWindow.xaml at {xamlPath}");

        XDocument doc = XDocument.Load(xamlPath);
        XNamespace wpfNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        // Rule 1: Every ProgressBar binding Value must explicitly set Mode=OneWay
        var progressBars = doc.Descendants(wpfNs + "ProgressBar").ToList();
        Assert.NotEmpty(progressBars);
        foreach (XElement bar in progressBars)
        {
            string? valAttr = bar.Attribute("Value")?.Value;
            if (valAttr is not null && valAttr.Contains("{Binding", StringComparison.Ordinal))
            {
                Assert.Contains("Mode=OneWay", valAttr, StringComparison.Ordinal);
            }
        }

        // Rule 2: No element may declare both DataContext="{Binding ...}" and Visibility="{Binding ...}" on the same element
        foreach (XElement elem in doc.Descendants())
        {
            string? dc = elem.Attribute("DataContext")?.Value;
            string? vis = elem.Attribute("Visibility")?.Value;
            if (dc is not null && dc.Contains("{Binding", StringComparison.Ordinal) &&
                vis is not null && vis.Contains("{Binding", StringComparison.Ordinal))
            {
                Assert.Fail($"Element <{elem.Name.LocalName}> combines DataContext='{dc}' and Visibility='{vis}' on the same node.");
            }
        }
    }

    [Fact]
    public void PreparationConflict_ShowsFailureAfterRescan_InsteadOf100PercentOrEnablingNext()
    {
        if (!OperatingSystem.IsWindows()) return;

        RunOnStaThread(() =>
        {
            SimulationScenario scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
            SystemEnvironmentSnapshot env = scenario.Environment with { IsSimulated = false, IsElevated = true };
            var logger = new SafeRedactingLogger();
            var disk = new StubDiskInspector(env);
            var partitions = new StubPartitionService();
            var bcd = new StubBcdBootService
            {
                State = new ActiveBcdState(false, "partition=C:", "partition=C:",
                    "{11111111-1111-1111-1111-111111111111}", null)
            };
            var readiness = new ReadinessEvaluator(new PartitionSizingCalculator());
            var guides = new SetupGuideGenerator();
            var orchestrator = new PreparationOrchestrator(disk, partitions,
                new StubIsoDeploymentService(), bcd, readiness, guides, logger);
            var vm = new MainViewModel(disk, new StubIsoInspector(scenario.SampleIso), bcd,
                readiness, guides, orchestrator, new CleanupService(disk, partitions, bcd, logger), logger,
                startInSimulationMode: false) { ShowInteractiveDialogs = false };

            AwaitWithDispatcher(vm.RefreshSystemScanAsync());
            vm.IsoPathInput = scenario.SampleIso.IsoPath;
            AwaitWithDispatcher(vm.ValidateSelectedIsoAsync());
            vm.IsDryRunMode = false;
            vm.ConfirmExternalBackup = true;
            vm.ConfirmWipeOldPartition = true;
            vm.ConfirmPreserveInstaller = true;
            vm.ConfirmBitLockerRecoveryKey = true;
            vm.CurrentWizardStep = 4;
            Assert.True(vm.CanStartPreparation());

            AwaitWithDispatcher(vm.RunPreparationAsync());
            Assert.NotNull(vm.LastPreparationResult);
            Assert.False(vm.LastPreparationResult!.Succeeded);
            Assert.InRange(vm.PrepProgressPercent, 0, 99);
            Assert.Equal(4, vm.CurrentWizardStep);
            Assert.False(vm.CanMoveNextStep());
            Assert.Contains("Existing SWBI boot entries", vm.BusyStatusText);
            Assert.Contains("2. Post-Install Cleanup", vm.PrepStepDetail);

            vm.SelectCleanupTabCommand.Execute(null);
            AwaitWithDispatcher(vm.CleanupVm.InspectAsync());
            Assert.Equal(CleanupSafetyState.ReadyToClean, vm.CleanupVm.Inspection?.State);
            Assert.Null(vm.CleanupVm.Inspection?.CandidatePartition);
            Assert.Equal("Repair Verified SWBI Boot Entries (Boot Only)", vm.CleanupVm.CleanupActionLabel);
        });
    }

    [Fact]
    public void BootRepairAssistant_DiagnosesBlockedFirmwareGuidWithoutEnablingDeletion()
    {
        if (!OperatingSystem.IsWindows()) return;

        RunOnStaThread(() =>
        {
            var scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
            var env = scenario.Environment with { IsSimulated = false, IsElevated = true };
            const string firmwareView = "{aaaaaaaa-1111-4444-8888-000000000010}";
            var boot = new StubBcdBootService
            {
                State = new ActiveBcdState(false, "partition=C:", "partition=C:",
                    null, null, firmwareView)
            };
            var diagnostics = new StubReadOnlyBootRepairDiagnostics();
            var cleanup = new CleanupService(new StubDiskInspector(env), new StubPartitionService(), boot,
                new SafeRedactingLogger());
            string? copied = null;
            var vm = new CleanupViewModel(cleanup, () => ExecutionMode.Live, () => null,
                diagnostics, text => copied = text);

            Assert.True(vm.DiagnoseBootCommand.CanExecute(null));
            Assert.False(vm.CanRunCleanup());
            AwaitWithDispatcher(vm.DiagnoseBootAsync());
            Assert.Equal(CleanupSafetyState.BlockedAmbiguousPartition, vm.Inspection?.State);
            Assert.Equal("Boot Repair Blocked — Review Read-Only Report", vm.CleanupActionLabel);
            Assert.Equal(firmwareView, diagnostics.LastInspected?.OrphanedBcdFirmwareGuid);
            Assert.True(vm.HasBootRepairReport);
            Assert.Contains("report ready", vm.StatusDetail, StringComparison.OrdinalIgnoreCase);
            Assert.True(vm.CopyBootReportCommand.CanExecute(null));
            vm.CopyBootReportCommand.Execute(null);
            Assert.Equal(vm.BootRepairReport, copied);
            vm.ConfirmedIndependentBoot = true;
            Assert.False(vm.CanRunCleanup());
            Assert.False(vm.ExecuteCleanupCommand.CanExecute(null));
            AwaitWithDispatcher(vm.ExecuteCleanupAsync()); // Direct calls also cannot bypass review.
            Assert.Equal(0, boot.RollbackCalls);

            AwaitWithDispatcher(vm.InspectAsync());
            Assert.False(vm.HasBootRepairReport);
            Assert.False(vm.ConfirmedIndependentBoot);
        });
    }

    [Fact]
    public void BootRepairAssistant_RepairsOnlyVerifiedBootEntriesAfterIndependentBootConfirmation()
    {
        if (!OperatingSystem.IsWindows()) return;

        RunOnStaThread(() =>
        {
            var scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
            var env = scenario.Environment with { IsSimulated = false, IsElevated = true };
            var boot = new StubBcdBootService
            {
                State = new ActiveBcdState(false, "partition=C:", "partition=C:",
                    "{11111111-1111-1111-1111-111111111111}",
                    "{22222222-2222-2222-2222-222222222222}")
            };
            var diagnostics = new StubReadOnlyBootRepairDiagnostics();
            var cleanup = new CleanupService(new StubDiskInspector(env), new StubPartitionService(), boot,
                new SafeRedactingLogger());
            var vm = new CleanupViewModel(cleanup, () => ExecutionMode.Live, () => null, diagnostics);

            AwaitWithDispatcher(vm.DiagnoseBootAsync());
            Assert.Equal(CleanupSafetyState.ReadyToClean, vm.Inspection?.State);
            Assert.False(vm.CanRunCleanup());
            AwaitWithDispatcher(vm.ExecuteCleanupAsync());
            Assert.Equal(0, boot.RollbackCalls);
            vm.ConfirmedIndependentBoot = true;
            Assert.True(vm.CanRunCleanup());
            AwaitWithDispatcher(vm.ExecuteCleanupAsync());
            Assert.Equal(1, boot.RollbackCalls);
            Assert.False(vm.HasBootRepairReport);
            Assert.Null(vm.Inspection); // A repair requires another fresh inspection before retrying.
        });
    }

    [Fact]
    public void ValueConverters_HandleNullEmptyAndUnsetValueCleanly()
    {
        var nullToVis = new NullOrEmptyToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed, nullToVis.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, nullToVis.Convert("   ", typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, nullToVis.Convert(DependencyProperty.UnsetValue, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, nullToVis.Convert("Ready", typeof(Visibility), null, CultureInfo.InvariantCulture));

        var boolToVis = new BooleanToVisibilityConverterEx();
        Assert.Equal(Visibility.Visible, boolToVis.Convert(true, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, boolToVis.Convert(true, typeof(Visibility), "Invert", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void MainWindow_FreshStartupAndAllScenarios_ZeroExceptionsAndZeroBindingErrors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunOnStaThread(() =>
        {
            EnsureWpfApplicationResourcesLoaded();

            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
            var bindingListener = new BindingTraceCollector();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);

            MainWindow? window = null;
            try
            {
                var scenario = SimulatedEnvironmentProvider.GetBuiltInScenarios()[0];
                var stubInspector = new StubDiskInspector(scenario.Environment);
                var stubIsoInspector = new StubIsoInspector(scenario.SampleIso);
                var stubPartition = new StubPartitionService();
                var stubDeploy = new StubIsoDeploymentService();
                var stubDrivers = new StubDriverHarvesterService();
                var stubBcd = new StubBcdBootService();
                var logger = new SafeRedactingLogger();

                var readiness = new ReadinessEvaluator(new PartitionSizingCalculator(), new SmartCompatibilityAnalyzer());
                var guideGen = new SetupGuideGenerator();
                var orchestrator = new PreparationOrchestrator(
                    stubInspector,
                    stubPartition,
                    stubDeploy,
                    stubBcd,
                    readiness,
                    guideGen,
                    logger,
                    driverHarvesterService: stubDrivers);
                var cleanup = new CleanupService(stubInspector, stubPartition, stubBcd, logger);

                // 1. Test LIVE-LIKE fresh startup (startInSimulationMode: false -> SelectedIso is NULL!)
                var vm = new MainViewModel(
                    stubInspector,
                    stubIsoInspector,
                    stubBcd,
                    readiness,
                    guideGen,
                    orchestrator,
                    cleanup,
                    logger,
                    startInCleanupMode: false,
                    startInSimulationMode: false)
                {
                    ShowInteractiveDialogs = false
                };

                window = new MainWindow
                {
                    Width = 1024,
                    Height = 720,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -3000,
                    Top = -3000,
                    ShowActivated = false,
                    DataContext = vm
                };

                window.Show();
                PumpDispatcher(window);

                // Console drawer starts collapsed on fresh startup to maximize step viewport height
                Assert.False(vm.IsLogExpanded);

                AwaitWithDispatcher(vm.InitializeAsync());
                PumpDispatcher(window);
                Assert.Null(vm.SelectedIso);

                // Verify zero top-bar collision/clipping at compact 1024x720 resolution (user's exact screen class)
                var topHeaderGrid = Assert.IsType<System.Windows.Controls.Grid>(window.FindName("TopHeaderGrid"));
                var brandPanel = Assert.IsType<System.Windows.Controls.Grid>(window.FindName("HeaderBrandPanel"));
                var actionPanel = Assert.IsType<System.Windows.Controls.StackPanel>(window.FindName("HeaderActionPanel"));
                Assert.True(topHeaderGrid.ActualWidth > 900);
                Assert.True(brandPanel.ActualWidth > 200);
                Assert.True(actionPanel.ActualWidth > 200);
                Assert.True(
                    brandPanel.ActualWidth + actionPanel.ActualWidth <= topHeaderGrid.ActualWidth + 2.0,
                    $"Header panels overflowed at 1024x720: brand={brandPanel.ActualWidth:F1}, action={actionPanel.ActualWidth:F1}, total={topHeaderGrid.ActualWidth:F1}");

                // Exercise custom log expander template (collapsed -> expanded -> collapsed)
                vm.IsLogExpanded = true;
                PumpDispatcher(window);
                vm.IsLogExpanded = false;
                PumpDispatcher(window);

                // Navigate every Wizard Step (1..5) while SelectedIso is null
                for (int step = 1; step <= 5; step++)
                {
                    vm.JumpToStepCommand.Execute(step.ToString());
                    PumpDispatcher(window);
                    Assert.Equal(step, vm.CurrentWizardStep);
                }

                // Switch to Post-Install Cleanup tab and verify visibility + inspection
                vm.SelectCleanupTabCommand.Execute(null);
                PumpDispatcher(window);
                Assert.True(vm.IsCleanupTabSelected);
                AwaitWithDispatcher(vm.CleanupVm.InspectAsync());
                PumpDispatcher(window);

                // Switch back to Prepare Installer tab
                vm.SelectWizardTabCommand.Execute(null);
                PumpDispatcher(window);
                Assert.True(vm.IsWizardTabSelected);

                // Verify ComboBox null-push guard on SelectedWorkflowOption
                var beforeOption = vm.SelectedWorkflowOption;
                Assert.NotNull(beforeOption);
                vm.SelectedWorkflowOption = null;
                Assert.NotNull(vm.SelectedWorkflowOption);

                // 2. Exercise all 8 hardware/OS simulation scenarios (including Custom/Modified Tiny11 ISO + LabConfig Bypass) end-to-end
                vm.UseSimulationScenario = true;
                PumpDispatcher(window);

                // Even with Sim Lab ComboBox visible at 1024x720, header panels must wrap cleanly without overflowing
                Assert.True(
                    brandPanel.ActualWidth + actionPanel.ActualWidth <= topHeaderGrid.ActualWidth + 2.0,
                    $"Header panels overflowed with Sim Lab active at 1024x720: brand={brandPanel.ActualWidth:F1}, action={actionPanel.ActualWidth:F1}, total={topHeaderGrid.ActualWidth:F1}");

                // Also verify full desktop 1280x820 layout
                window.Width = 1280;
                window.Height = 820;
                PumpDispatcher(window);
                bool verifiedCustomScenario = false;
                foreach (var sim in vm.SimulationScenarios)
                {
                    vm.SelectedScenario = sim;
                    PumpDispatcher(window);

                    if (sim.Id == "custom-tiny11-older-pc")
                    {
                        verifiedCustomScenario = true;
                        Assert.True(vm.BypassWin11HardwareChecks);
                        Assert.True(vm.SelectedIso?.IsCustomOrModifiedIso);
                        Assert.Contains("Custom / Modified ISO", vm.IsoSummaryBanner);
                    }

                    for (int step = 1; step <= 5; step++)
                    {
                        vm.JumpToStepCommand.Execute(step.ToString());
                        PumpDispatcher(window);
                    }

                    vm.ConfirmExternalBackup = true;
                    vm.ConfirmWipeOldPartition = true;
                    vm.ConfirmPreserveInstaller = true;
                    vm.ConfirmBitLockerRecoveryKey = true;

                    if (vm.CanStartPreparation())
                    {
                        AwaitWithDispatcher(vm.RunPreparationAsync());
                        PumpDispatcher(window);
                        Assert.Equal(100, vm.PrepProgressPercent);
                        Assert.Equal(5, vm.CurrentWizardStep);
                        Assert.NotEmpty(vm.LastPrepActions);

                        if (sim.Id == "custom-tiny11-older-pc")
                        {
                            Assert.Contains(vm.LastPrepActions, a => a.Contains("LabConfig", StringComparison.OrdinalIgnoreCase));
                        }

                        string? cheat = vm.GetFormattedCheatSheetText();
                        Assert.NotNull(cheat);
                        Assert.Contains(SwbiBrand.PartitionVolumeLabel, cheat);
                        Assert.Contains(SwbiBrand.BootChooserEntryName, cheat);
                    }

                    // Test Cleanup tab on each scenario
                    vm.SelectCleanupTabCommand.Execute(null);
                    AwaitWithDispatcher(vm.CleanupVm.InspectAsync());
                    if (sim == vm.SimulationScenarios[0])
                    {
                        AwaitWithDispatcher(vm.CleanupVm.DiagnoseBootAsync());
                        PumpDispatcher(window);
                        Assert.Contains("SIMULATION ONLY", vm.CleanupVm.BootRepairReport);
                    }
                    vm.CleanupVm.ConfirmedIndependentBoot = true;
                    if (vm.CleanupVm.CanRunCleanup())
                    {
                        AwaitWithDispatcher(vm.CleanupVm.ExecuteCleanupAsync());
                        Assert.NotNull(vm.CleanupVm.LastCleanupSummary);
                    }

                    PumpDispatcher(window);
                    vm.SelectWizardTabCommand.Execute(null);
                    PumpDispatcher(window);
                }

                Assert.True(verifiedCustomScenario);
                Assert.Empty(bindingListener.Messages);
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
                window?.Close();
                Application.Current?.Shutdown();
            }
        });
    }

    private static void EnsureWpfApplicationResourcesLoaded()
    {
        lock (AppInitLock)
        {
            if (Application.Current is null)
            {
                var app = new App
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                app.InitializeComponent();
            }
        }
    }

    private static void AwaitWithDispatcher(Task task)
    {
        if (!task.IsCompleted)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)),
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
    }

    private static void PumpDispatcher(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new DispatcherOperationCallback(_ =>
            {
                frame.Continue = false;
                return null;
            }),
            null);
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnStaThread(Action action)
    {
        ExceptionDispatchInfo? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception ex)
            {
                captured = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45)))
        {
            throw new TimeoutException("STA WPF UI test timed out after 45 seconds.");
        }

        captured?.Throw();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "UsbFreeWindowsInstaller.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate UsbFreeWindowsInstaller.sln.");
    }

    private sealed class BindingTraceCollector : TraceListener
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _messages.Enqueue(message.Trim());
            }
        }
    }

    private sealed class StubDiskInspector(SystemEnvironmentSnapshot snapshot) : IDiskInspector
    {
        public Task<SystemEnvironmentSnapshot> InspectEnvironmentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class StubIsoInspector(IsoInspectionResult iso) : IIsoInspector
    {
        public Task<IsoInspectionResult> InspectIsoAsync(
            string isoPath,
            bool computeSha256,
            IProgress<int>? hashProgress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(iso);
    }

    private sealed class StubPartitionService : IPartitionService
    {
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
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Live partition mutation is not executed during DryRun UI tests.");

        public Task WriteInstallerManifestAsync(string installerDriveLetter, InstallerManifest manifest, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteInstallerPartitionAsync(int diskNumber, int installerPartitionNumber, string expectedPartitionGuid, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ExtendOsPartitionIntoReclaimedSpaceAsync(int diskNumber, int osPartitionNumber, CancellationToken cancellationToken = default, long expectedOsPartitionOffsetBytes = 0) =>
            Task.CompletedTask;
    }

    private sealed class StubIsoDeploymentService : IIsoDeploymentService
    {
        public Task DeployIsoToInstallerPartitionAsync(
            IsoInspectionResult iso,
            string targetDriveLetter,
            int copyBufferSizeBytes = 1048576,
            bool writeEditionUnlockEiCfg = true,
            IProgress<DeploymentProgress>? progress = null,
            CancellationToken cancellationToken = default,
            bool bypassWin11HardwareChecks = false) =>
            Task.CompletedTask;

        public Task VerifyDeployedBootAndInstallFilesAsync(
            IsoInspectionResult iso,
            string targetDriveLetter,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubDriverHarvesterService : IDriverHarvesterService
    {
        public Task<DriverHarvestSummary> ExportAndInjectDriversAsync(
            string installerDriveLetter,
            bool includeNetworkDrivers,
            bool injectStorageIntoBootWim,
            IProgress<DeploymentProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? activeControllerServiceName = null) =>
            Task.FromResult(new DriverHarvestSummary(2, 1, 1024 * 1024, injectStorageIntoBootWim, true));

        public Task LaunchInPlaceUpgradeSetupAsync(string installerDriveLetter, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubReadOnlyBootRepairDiagnostics : IReadOnlyBootRepairDiagnostics
    {
        public CleanupInspectionResult? LastInspected { get; private set; }

        public Task<string> CaptureAsync(CleanupInspectionResult inspection, CancellationToken cancellationToken = default)
        {
            LastInspected = inspection;
            return Task.FromResult($"READ ONLY: inspected {inspection.OrphanedBcdFirmwareGuid}; no boot settings changed.");
        }
    }

    private sealed class StubBcdBootService : IBcdBootService
    {
        public ActiveBcdState State { get; init; } = new(false, "partition=C:", "partition=C:", null, null, null);
        public int RollbackCalls { get; private set; }

        public Task<BcdProvisionResult> ConfigureOneTimeWinPeBootAsync(
            string installerDriveLetter,
            string backupDirectory,
            FirmwareMode firmwareMode = FirmwareMode.Uefi,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BcdProvisionResult("bcd.bak", "{11111111-1111-1111-1111-111111111111}",
                "{22222222-2222-2222-2222-222222222222}", null));

        public Task RollbackBcdEntriesAsync(
            string? osLoaderGuid,
            string? sdiDeviceGuid,
            CancellationToken cancellationToken = default,
            string? firmwareBootGuid = null)
        {
            RollbackCalls++;
            return Task.CompletedTask;
        }

        public Task<ActiveBcdState> InspectActiveBcdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(State);

        public Task RebootSystemAsync(int delaySeconds = 5, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
