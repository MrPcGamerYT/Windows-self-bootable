using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using UsbFreeInstaller.App.Infrastructure;
using UsbFreeInstaller.App.ViewModels;
using UsbFreeInstaller.App.Views;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;

namespace UsbFreeInstaller.App;

public partial class App : Application
{
    private SafeRedactingLogger? _logger;
    private readonly ConcurrentQueue<string> _unhandledErrors = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0] == StandaloneUpdateCoordinator.ApplySwitch)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                await StandaloneUpdateCoordinator.ApplyFromArgumentsAsync(e.Args);
                Shutdown(0);
            }
            catch (Exception error)
            {
                MessageBox.Show($"The update could not be completed. The original or its backup has been retained when possible.\n\n{error.Message}",
                    "SWBI — Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }

        bool isSmokeTest = e.Args.Any(a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase) || a.Equals("/smoke-test", StringComparison.OrdinalIgnoreCase));
        bool skipAutoElevate = isSmokeTest ||
                               e.Args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase) ||
                                               a.Equals("--elevated-relaunch", StringComparison.OrdinalIgnoreCase));
        string? smokeReportPath = GetOptionValue(e.Args, "--smoke-report");

        DispatcherUnhandledException += (_, args) =>
        {
            string msg = $"{args.Exception.GetType().Name}: {args.Exception.Message}";
            _unhandledErrors.Enqueue(msg);
            (_logger as ISafeLogger)?.Error("Crash", $"Unhandled UI exception: {args.Exception}");

            if (isSmokeTest)
            {
                args.Handled = true;
                return;
            }

            MessageBox.Show(
                $"An unexpected UI error occurred:\n\n{args.Exception.Message}\n\nSession logs are saved in:\n{_logger?.LogFilePath ?? "C:\\ProgramData\\SWBI\\Work\\logs"}",
                $"{SwbiBrand.ShortName} — Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;

            if (MainWindow is not { IsVisible: true })
            {
                Shutdown(1);
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                _unhandledErrors.Enqueue($"{ex.GetType().Name}: {ex.Message}");
                (_logger as ISafeLogger)?.Error("Fatal", $"Unhandled domain exception: {ex}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _unhandledErrors.Enqueue($"UnobservedTaskException: {args.Exception.Message}");
            (_logger as ISafeLogger)?.Error("Task", $"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        DataBindingErrorCollector? bindingCollector = null;
        if (isSmokeTest)
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
            bindingCollector = new DataBindingErrorCollector();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingCollector);
        }

        try
        {
            SwbiWorkDirectory.EnsureProcessTempVariablesHealthy();

            // If launched directly from portable folder without UAC elevation, prompt UAC once —
            // falling back cleanly to Dry-Run GUI if UAC/Appinfo is disabled or user clicks No.
            if (OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess && !skipAutoElevate)
            {
                if (TryRelaunchSelfAsAdmin(e.Args))
                {
                    Shutdown(0);
                    return;
                }
            }

            string logDir = SwbiWorkDirectory.EnsureSafeWorkDirectory("logs");
            _logger = new SafeRedactingLogger(logDir);
            ISafeLogger safeLogger = _logger;
            safeLogger.Info("Startup", $"{SwbiBrand.BrandedTitle} v{SwbiBrand.CurrentVersion} starting up.");

            // Any exception escaping an async command handler is logged and recorded, so the failure is
            // never reduced to a generic "unexpected UI error" dialog with no trace.
            AsyncRelayCommand.UnhandledExceptionHandler = ex =>
            {
                _unhandledErrors.Enqueue($"{ex.GetType().Name}: {ex.Message}");
                safeLogger.Error("Command", $"Unhandled exception from a UI command: {ex}");
            };

            // Only one SWBI window may prepare or clean disks at a time.
            if (!isSmokeTest && !SingleInstanceGuard.TryAcquire(out string singleInstanceReason))
            {
                MessageBox.Show(
                    singleInstanceReason + "\n\nClose the other SWBI window before preparing or cleaning up, " +
                    "so two instances cannot modify the same disk at once.",
                    $"{SwbiBrand.ShortName} — Already Running",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown(1);
                return;
            }

            bool startCleanup = e.Args.Any(a =>
                a.Equals("--cleanup", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/cleanup", StringComparison.OrdinalIgnoreCase));

            bool startSimulate = e.Args.Any(a =>
                a.Equals("--simulate", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/simulate", StringComparison.OrdinalIgnoreCase));
            // Composition root (manual DI — zero external container overhead)
            var runner = new ProcessRunner(_logger);
            var diskInspector = new WindowsDiskInspector(runner, _logger);
            var isoInspector = new WindowsIsoInspector(runner, _logger);
            var partitionService = new WindowsPartitionService(runner, _logger);
            var deploymentService = new WindowsIsoDeploymentService(runner, _logger);
            var driverHarvester = new WindowsDriverHarvesterService(runner, _logger);
            var bcdService = new WindowsBcdBootService(runner, _logger);

            var sizingCalculator = new PartitionSizingCalculator();
            var smartAnalyzer = new SmartCompatibilityAnalyzer();
            var readinessEvaluator = new ReadinessEvaluator(sizingCalculator, smartAnalyzer);
            var setupGuideGenerator = new SetupGuideGenerator();

            var orchestrator = new PreparationOrchestrator(
                diskInspector,
                partitionService,
                deploymentService,
                bcdService,
                readinessEvaluator,
                setupGuideGenerator,
                _logger,
                driverHarvesterService: driverHarvester);

            var cleanupService = new CleanupService(
                diskInspector,
                partitionService,
                bcdService,
                _logger);

            var mainVm = new MainViewModel(
                diskInspector,
                isoInspector,
                bcdService,
                readinessEvaluator,
                setupGuideGenerator,
                orchestrator,
                cleanupService,
                _logger,
                startInCleanupMode: startCleanup,
                startInSimulationMode: startSimulate);

            var window = new MainWindow
            {
                DataContext = mainVm
            };

            MainWindow = window;
            window.Show();

            await mainVm.InitializeAsync();

            if (isSmokeTest)
            {
                int exitCode = await RunEndToEndGuiSmokeTestAsync(
                    window,
                    mainVm,
                    bindingCollector!,
                    smokeReportPath);
                window.Close();
                Shutdown(exitCode);
            }
        }
        catch (Exception ex)
        {
            _unhandledErrors.Enqueue($"{ex.GetType().Name}: {ex.Message}");
            (_logger as ISafeLogger)?.Error("StartupFatal", $"Startup exception: {ex}");

            if (isSmokeTest)
            {
                WriteSmokeReport(smokeReportPath, passed: false, bindingCollector?.Errors ?? Array.Empty<string>(), _unhandledErrors.ToArray(), "Fatal exception during OnStartup");
                Shutdown(2);
                return;
            }

            MessageBox.Show(
                $"Failed to initialize {SwbiBrand.BrandedTitle}:\n\n{ex.Message}\n\nLog file:\n{_logger?.LogFilePath ?? "C:\\ProgramData\\SWBI\\Work\\logs"}",
                $"{SwbiBrand.ShortName} — Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public static bool TryRelaunchSelfAsAdmin(IReadOnlyList<string> args)
    {
        try
        {
            string? selfExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(selfExe) || !File.Exists(selfExe))
            {
                return false;
            }

            string joinedArgs = string.Join(" ", new[] { "--elevated-relaunch" }.Concat(
                args.Where(a => !a.Equals("--elevated-relaunch", StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

            var psi = new ProcessStartInfo
            {
                FileName = selfExe,
                Arguments = joinedArgs,
                WorkingDirectory = Path.GetDirectoryName(selfExe) ?? AppContext.BaseDirectory,
                Verb = "runas",
                UseShellExecute = true
            };

            using Process? started = Process.Start(psi);
            return started is not null;
        }
        catch
        {
            return false;
        }
    }

    private async Task<int> RunEndToEndGuiSmokeTestAsync(
        MainWindow window,
        MainViewModel vm,
        DataBindingErrorCollector bindingCollector,
        string? smokeReportPath)
    {
        vm.ShowInteractiveDialogs = false;
        await FlushDispatcherAsync(window);

        // Smoke-test the native launcher-to-child identity contract used by the updater.
        try { _ = UsbFreeInstaller.Core.Services.Updates.StandaloneLauncher.FromEnvironment(); }
        catch (Exception ex) { _unhandledErrors.Enqueue($"Standalone update identity failed: {ex.Message}"); }

        // 1. Verify fresh live-scan startup state (SelectedIso == null) across all 5 steps & Cleanup tab
        if (OperatingSystem.IsWindows() && !vm.UseSimulationScenario)
        {
            if (vm.EnvironmentSnapshot?.TargetDisk is null || vm.EnvironmentSnapshot?.TargetOsPartition is null)
            {
                _unhandledErrors.Enqueue("Live Windows disk scan failed to resolve TargetDisk or C: TargetOsPartition.");
            }
        }

        for (int step = 1; step <= 5; step++)
        {
            vm.JumpToStepCommand.Execute(step.ToString());
            await FlushDispatcherAsync(window);
        }

        vm.IsCleanupTabSelected = true;
        await vm.CleanupVm.InspectAsync();
        await FlushDispatcherAsync(window);

        vm.SelectWizardTabCommand.Execute(null);
        await FlushDispatcherAsync(window);

        // 2. Exercise all 7 built-in hardware/OS scenarios (NVMe Upgrade, VMD Win11->10 Downgrade, HDD, SATA SSD, BitLocker, Low Space, Post-Install Cleanup)
        vm.UseSimulationScenario = true;
        foreach (var scenario in vm.SimulationScenarios)
        {
            vm.SelectedScenario = scenario;
            await FlushDispatcherAsync(window);

            for (int step = 1; step <= 5; step++)
            {
                vm.JumpToStepCommand.Execute(step.ToString());
                await FlushDispatcherAsync(window);
            }

            // Exercise workflow options dropdown selection changes
            foreach (var wf in vm.AvailableWorkflowOptions.ToList())
            {
                vm.SelectedWorkflowOption = wf;
                await FlushDispatcherAsync(window);
            }

            // If dry-run preparation is allowed for this scenario, run full preparation + cheat sheet generation
            vm.ConfirmExternalBackup = true;
            vm.ConfirmWipeOldPartition = true;
            vm.ConfirmPreserveInstaller = true;
            vm.ConfirmBitLockerRecoveryKey = true;

            if (vm.CanStartPreparation())
            {
                await vm.RunPreparationAsync();
                await FlushDispatcherAsync(window);

                string? cheatSheet = vm.GetFormattedCheatSheetText();
                if (string.IsNullOrWhiteSpace(cheatSheet) ||
                    !cheatSheet.Contains(SwbiBrand.PartitionVolumeLabel, StringComparison.Ordinal) ||
                    !cheatSheet.Contains(SwbiBrand.BootChooserEntryName, StringComparison.Ordinal))
                {
                    _unhandledErrors.Enqueue($"Cheat sheet validation failed for scenario {scenario.Id}");
                }
            }

            // Exercise Cleanup tab inspection + Dry-Run cleanup execution
            vm.IsCleanupTabSelected = true;
            await vm.CleanupVm.InspectAsync();
            vm.CleanupVm.ConfirmedIndependentBoot = true;
            if (vm.CleanupVm.CanRunCleanup())
            {
                await vm.CleanupVm.ExecuteCleanupAsync();
            }

            await FlushDispatcherAsync(window);
            vm.SelectWizardTabCommand.Execute(null);
            await FlushDispatcherAsync(window);
        }

        string[] bindingErrors = bindingCollector.Errors.ToArray();
        string[] runtimeErrors = _unhandledErrors.ToArray();
        bool passed = bindingErrors.Length == 0 && runtimeErrors.Length == 0;

        WriteSmokeReport(
            smokeReportPath,
            passed,
            bindingErrors,
            runtimeErrors,
            passed
                ? $"SWBI v{SwbiBrand.CurrentVersion} GUI smoke test passed: live scan + {vm.SimulationScenarios.Count} scenarios + 5 wizard steps + cleanup verified with 0 binding/runtime errors."
                : "SWBI GUI smoke test detected binding or runtime errors.");

        return passed ? 0 : 2;
    }

    private static async Task FlushDispatcherAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private static string? GetOptionValue(string[] args, string optionName)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(optionName, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void WriteSmokeReport(
        string? reportPath,
        bool passed,
        IReadOnlyList<string> bindingErrors,
        IReadOnlyList<string> runtimeErrors,
        string summary)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return;
        }

        string? dir = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var payload = new
        {
            Passed = passed,
            Summary = summary,
            BindingErrorCount = bindingErrors.Count,
            BindingErrors = bindingErrors,
            RuntimeErrorCount = runtimeErrors.Count,
            RuntimeErrors = runtimeErrors,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (_logger as ISafeLogger)?.Info("Shutdown", $"{SwbiBrand.BrandedTitle} exiting cleanly with code {e.ApplicationExitCode}.");
        base.OnExit(e);
    }

    private sealed class DataBindingErrorCollector : TraceListener
    {
        private readonly ConcurrentQueue<string> _errors = new();

        public IReadOnlyList<string> Errors => _errors.ToArray();

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _errors.Enqueue(message.Trim());
            }
        }
    }
}
