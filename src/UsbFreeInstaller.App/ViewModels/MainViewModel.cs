using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using UsbFreeInstaller.App.Infrastructure;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Simulation;
using UsbFreeInstaller.Core.Services.Updates;

namespace UsbFreeInstaller.App.ViewModels;

public sealed record WorkflowOptionItem(
    InstallWorkflowChoice Value,
    string DisplayLabel);

public sealed class MainViewModel : ObservableObject
{
    private readonly IDiskInspector _diskInspector;
    private readonly IIsoInspector _isoInspector;
    private readonly IBcdBootService _bcdBootService;
    private readonly ReadinessEvaluator _readinessEvaluator;
    private readonly SetupGuideGenerator _guideGenerator;
    private readonly PreparationOrchestrator _orchestrator;
    private readonly ISafeLogger _logger;

    private CancellationTokenSource? _activeOperationCts;

    private bool _isCleanupTabSelected;
    private int _currentWizardStep = 1;
    private bool _isDryRunMode = true;
    private bool _useSimulationScenario;
    private SimulationScenario? _selectedScenario;

    private SystemEnvironmentSnapshot? _environment;
    private IsoInspectionResult? _selectedIso;
    private ReadinessReport? _readiness;
    private SetupPartitionGuide? _setupGuide;
    private PreparationResult? _preparationResult;

    private string _isoPathInput = string.Empty;
    private bool _computeIsoSha256;
    private int _isoHashPercent;
    private bool _isBusy;
    private string _busyStatusText = "Ready.";
    private bool _isUpdateRunning;
    private int _updateProgressPercent;
    private string _updateStatusText = "Updates are checked on request.";

    // Smart PC & OS Tailoring options
    private WorkflowOptionItem? _selectedWorkflowOption;
    private bool _exportHostDrivers = true;
    private bool _injectBootWimStorageDrivers;
    private bool _writeEiCfgEditionUnlock = true;
    private bool _runDrivePreShrinkOptimize;
    private bool _bypassWin11HardwareChecks;

    // User safety confirmations
    private bool _confirmExternalBackup;
    private bool _confirmWipeOldPartition;
    private bool _confirmPreserveInstaller;
    private bool _confirmBitLockerRecoveryKey;
    private bool _autoSuspendBitLocker = true;

    // Preparation progress
    private int _prepProgressPercent;
    private string _prepStepTitle = "Waiting to start";
    private string _prepStepDetail = "Complete Steps 1–3 and acknowledge all safety checks above.";

    private bool _isLogExpanded;
    private string _latestLogLineText = "Ready — awaiting system inspection...";

    public bool ShowInteractiveDialogs { get; set; } = true;

    public IReadOnlyList<SimulationScenario> SimulationScenarios { get; } =
        SimulatedEnvironmentProvider.GetBuiltInScenarios();

    public ObservableCollection<WorkflowOptionItem> AvailableWorkflowOptions { get; } = new();
    public ObservableCollection<SystemCheckItem> ReadinessChecks { get; } = new();
    public ObservableCollection<WimEditionInfo> IsoEditions { get; } = new();
    public ObservableCollection<PartitionBarSegment> BeforeDiskSegments { get; } = new();
    public ObservableCollection<PartitionBarSegment> AfterPrepDiskSegments { get; } = new();
    public ObservableCollection<PartitionBarSegment> SetupDeletedDiskSegments { get; } = new();
    public ObservableCollection<SetupPartitionRow> SetupGuideRows { get; } = new();
    public ObservableCollection<string> CriticalSetupRules { get; } = new();
    public ObservableCollection<string> LastPrepActions { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public CleanupViewModel CleanupVm { get; }

    // Commands
    public RelayCommand SelectWizardTabCommand { get; }
    public RelayCommand SelectCleanupTabCommand { get; }
    public RelayCommand NextStepCommand { get; }
    public RelayCommand PreviousStepCommand { get; }
    public RelayCommand JumpToStepCommand { get; }

    public AsyncRelayCommand RefreshSystemScanCommand { get; }
    public RelayCommand BrowseIsoCommand { get; }
    public AsyncRelayCommand ValidateIsoCommand { get; }
    public RelayCommand ExportCheatSheetCommand { get; }
    public AsyncRelayCommand StartPreparationCommand { get; }
    public RelayCommand CancelOperationCommand { get; }
    public AsyncRelayCommand RebootIntoSetupCommand { get; }
    public RelayCommand CopyLogsCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand ElevateAsAdminCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public bool IsCleanupTabSelected
    {
        get => _isCleanupTabSelected;
        set
        {
            if (SetProperty(ref _isCleanupTabSelected, value))
            {
                OnPropertyChanged(nameof(IsWizardTabSelected));
            }
        }
    }

    public bool IsWizardTabSelected => !IsCleanupTabSelected;

    public int CurrentWizardStep
    {
        get => _currentWizardStep;
        set
        {
            int clamped = Math.Clamp(value, 1, 5);
            if (SetProperty(ref _currentWizardStep, clamped))
            {
                OnPropertyChanged(nameof(IsStep1));
                OnPropertyChanged(nameof(IsStep2));
                OnPropertyChanged(nameof(IsStep3));
                OnPropertyChanged(nameof(IsStep4));
                OnPropertyChanged(nameof(IsStep5));
                OnPropertyChanged(nameof(IsStep1Done));
                OnPropertyChanged(nameof(IsStep2Done));
                OnPropertyChanged(nameof(IsStep3Done));
                OnPropertyChanged(nameof(IsStep4Done));
                NotifyNavigationStateChanged();
            }
        }
    }

    public bool IsUpdateRunning
    {
        get => _isUpdateRunning;
        private set => SetProperty(ref _isUpdateRunning, value);
    }

    public int UpdateProgressPercent
    {
        get => _updateProgressPercent;
        private set => SetProperty(ref _updateProgressPercent, value);
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    public string VersionBadgeText => $"v{SwbiBrand.CurrentVersion}";

    public bool IsStep1 => CurrentWizardStep == 1;
    public bool IsStep2 => CurrentWizardStep == 2;
    public bool IsStep3 => CurrentWizardStep == 3;
    public bool IsStep4 => CurrentWizardStep == 4;
    public bool IsStep5 => CurrentWizardStep == 5;

    public bool IsStep1Done => CurrentWizardStep > 1;
    public bool IsStep2Done => CurrentWizardStep > 2;
    public bool IsStep3Done => CurrentWizardStep > 3;
    public bool IsStep4Done => CurrentWizardStep > 4;

    public bool IsDryRunMode
    {
        get => _isDryRunMode;
        set
        {
            if (SetProperty(ref _isDryRunMode, value))
            {
                OnPropertyChanged(nameof(IsLiveHardwareMode));
                OnPropertyChanged(nameof(ExecutionModeShortText));
                OnPropertyChanged(nameof(ExecutionModeBadgeText));
                OnPropertyChanged(nameof(PreparationButtonLabel));
                OnPropertyChanged(nameof(PreparationGateHintText));
                StartPreparationCommand.NotifyCanExecuteChanged();
                RebootIntoSetupCommand.NotifyCanExecuteChanged();
                NextStepCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsLiveHardwareMode => !IsDryRunMode;

    public string ExecutionModeShortText =>
        IsDryRunMode ? "SAFE DRY-RUN" : "LIVE HARDWARE";

    public string ExecutionModeBadgeText =>
        IsDryRunMode ? "DRY-RUN MODE (SAFE — 0 DISK WRITES)" : "LIVE HARDWARE MODE (MODIFIES PARTITIONS & BCD)";

    public string PreparationButtonLabel =>
        IsDryRunMode ? "Run Safe SWBI Dry-Run Simulation" : $"Build '{SwbiBrand.PartitionVolumeLabel}' & Arm SWBI Boot";

    public bool UseSimulationScenario
    {
        get => _useSimulationScenario;
        set
        {
            if (SetProperty(ref _useSimulationScenario, value))
            {
                OnPropertyChanged(nameof(ShowElevateButton));
                ElevateAsAdminCommand.NotifyCanExecuteChanged();
                if (value)
                {
                    IsDryRunMode = true;
                    SelectedScenario ??= SimulationScenarios[0];
                    ApplySelectedSimulation();
                }
                else
                {
                    _ = RefreshSystemScanAsync();
                }
            }
        }
    }

    public SimulationScenario? SelectedScenario
    {
        get => _selectedScenario;
        set
        {
            if (SetProperty(ref _selectedScenario, value) && UseSimulationScenario && value is not null)
            {
                ApplySelectedSimulation();
            }
        }
    }

    public SystemEnvironmentSnapshot? EnvironmentSnapshot
    {
        get => _environment;
        private set
        {
            if (SetProperty(ref _environment, value))
            {
                OnPropertyChanged(nameof(TargetDiskHeader));
                OnPropertyChanged(nameof(PcProfileSummaryText));
                OnPropertyChanged(nameof(DriveTypeBadgeText));
                OnPropertyChanged(nameof(ShrinkSummaryText));
                OnPropertyChanged(nameof(StatOsTitleText));
                OnPropertyChanged(nameof(StatOsSubText));
                OnPropertyChanged(nameof(StatHwTitleText));
                OnPropertyChanged(nameof(StatHwSubText));
                OnPropertyChanged(nameof(StatDiskTitleText));
                OnPropertyChanged(nameof(StatDiskSubText));
                OnPropertyChanged(nameof(StatShrinkTitleText));
                OnPropertyChanged(nameof(StatShrinkSubText));
                OnPropertyChanged(nameof(ElevationPillText));
                OnPropertyChanged(nameof(ShowElevateButton));
                OnPropertyChanged(nameof(PreparationGateHintText));
                ElevateAsAdminCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatOsTitleText =>
        EnvironmentSnapshot?.CurrentWindows?.ProductName ?? "Scanning OS...";

    public string StatOsSubText =>
        EnvironmentSnapshot?.CurrentWindows is { } w
            ? $"{w.DisplayVersion} (Build {w.BuildNumber}) • {EnvironmentSnapshot.HostArchitecture}"
            : "Detecting build & architecture";

    public string StatHwTitleText =>
        EnvironmentSnapshot?.Hardware is { } h
            ? $"RAM {ByteFormatting.FormatGb(h.TotalRamBytes)} • {(h.TpmPresent ? $"TPM {h.TpmVersion}" : "No TPM 2.0")}"
            : "Detecting CPU & RAM...";

    public string StatHwSubText =>
        EnvironmentSnapshot?.Hardware?.CpuName ?? "Inspecting processor & firmware";

    public string StatDiskTitleText =>
        EnvironmentSnapshot?.TargetDisk is { } d
            ? $"Disk {d.DiskNumber} • {d.SizeDisplay} ({d.PartitionStyle})"
            : "Scanning Disk...";

    public string StatDiskSubText =>
        EnvironmentSnapshot?.TargetDisk is { } d
            ? $"{d.FriendlyName} ({d.SectorGeometryDisplay})"
            : "Inspecting storage controller";

    public string StatShrinkTitleText =>
        EnvironmentSnapshot?.ShrinkInfo is { } s
            ? $"{ByteFormatting.FormatGb(s.SafeUsableShrinkBytes)} Usable"
            : "Calculating...";

    public string StatShrinkSubText =>
        EnvironmentSnapshot?.ShrinkInfo is { } s
            ? $"Free: {ByteFormatting.FormatGb(s.VolumeFreeBytes)} (8 GB OS reserve kept)"
            : "Checking NTFS shrink bounds";

    public string ElevationPillText =>
        EnvironmentSnapshot?.IsElevated == true ? "Administrator" : "Not Elevated (Dry-Run Only)";

    public bool ShowElevateButton =>
        OperatingSystem.IsWindows() && EnvironmentSnapshot is { IsElevated: false } && !UseSimulationScenario;

    public string DriveTypeBadgeText =>
        EnvironmentSnapshot?.TargetDisk?.DriveKindDisplay.ToUpperInvariant() ?? "SCANNING DRIVE";

    public string TargetDiskHeader =>
        EnvironmentSnapshot?.TargetDisk is { } d
            ? $"Disk {d.DiskNumber}: {d.FriendlyName} ({d.SizeDisplay}, {d.PartitionStyle}, {d.SectorGeometryDisplay})"
            : "Target system disk not yet scanned";

    public string PcProfileSummaryText
    {
        get
        {
            if (EnvironmentSnapshot is null)
            {
                return "Detecting PC hardware, current Windows edition/build, and storage controller...";
            }

            string osText = EnvironmentSnapshot.CurrentWindows?.SummaryDisplay ?? "Windows";
            string hwText = EnvironmentSnapshot.Hardware?.SummaryDisplay ?? EnvironmentSnapshot.HostArchitecture;
            return $"Current OS: {osText}  •  {hwText}";
        }
    }

    public string ShrinkSummaryText
    {
        get
        {
            if (EnvironmentSnapshot?.ShrinkInfo is not { } s)
            {
                return "Scan system disk to calculate free space vs. true shrinkable space.";
            }

            return $"Current C: Size: {ByteFormatting.FormatGb(s.CurrentSizeBytes)}  •  " +
                   $"Free Space: {ByteFormatting.FormatGb(s.VolumeFreeBytes)}  •  " +
                   $"Windows Max Shrinkable: {ByteFormatting.FormatGb(s.RawMaxShrinkableBytes)}  •  " +
                   $"Safe Usable Shrink (keeping 8 GB OS reserve): {ByteFormatting.FormatGb(s.SafeUsableShrinkBytes)}";
        }
    }

    public IsoInspectionResult? SelectedIso
    {
        get => _selectedIso;
        private set
        {
            if (SetProperty(ref _selectedIso, value))
            {
                OnPropertyChanged(nameof(IsoSummaryBanner));
                OnPropertyChanged(nameof(SelectedIsoSha256));
                OnPropertyChanged(nameof(PreparationGateHintText));
            }
        }
    }

    public string? SelectedIsoSha256 =>
        SelectedIso?.Sha256Hex is { Length: > 0 } hash ? $"SHA-256: {hash}" : null;

    public string IsoSummaryBanner =>
        SelectedIso is null
            ? "No Windows ISO validated yet."
            : SelectedIso.IsValid
                ? $"[{SelectedIso.IsoVariantBadge}] {System.IO.Path.GetFileName(SelectedIso.IsoPath)} ({SelectedIso.FileSizeDisplay}, {SelectedIso.Architecture}) — Payload: {SelectedIso.PayloadRelativePath} ({SelectedIso.PayloadSizeDisplay}) — FAT32 Strategy: {SelectedIso.SplitStrategy}"
                : $"Invalid ISO: {string.Join(" ", SelectedIso.ValidationErrors)}";

    public ReadinessReport? Readiness
    {
        get => _readiness;
        private set
        {
            if (SetProperty(ref _readiness, value))
            {
                OnPropertyChanged(nameof(RequiresBitLockerConfirmation));
                OnPropertyChanged(nameof(SizingSummaryText));
                OnPropertyChanged(nameof(TransitionBadgeText));
                OnPropertyChanged(nameof(TransitionHeadlineText));
                OnPropertyChanged(nameof(TransitionExplanationText));
                OnPropertyChanged(nameof(DriveTuningSummaryText));
                OnPropertyChanged(nameof(PreparationGateHintText));
                NotifyNavigationStateChanged();
            }
        }
    }

    public bool HasSetupGuide => _setupGuide is not null;

    public string TransitionBadgeText =>
        Readiness?.Compatibility?.TransitionBadge ?? "SMART ANALYZER";

    public string TransitionHeadlineText =>
        Readiness?.Compatibility?.TransitionHeadline ?? "Select a Windows ISO to analyze Upgrade, Clean Install, or Downgrade ('Degrade') setup.";

    public string TransitionExplanationText =>
        Readiness?.Compatibility?.TransitionExplanation ?? string.Empty;

    public string DriveTuningSummaryText =>
        Readiness?.Compatibility?.DrivePlan.TuningExplanation ?? "Drive auto-tuning activates after scanning your C: storage device.";

    public bool RequiresBitLockerConfirmation =>
        Readiness?.RequiresBitLockerRecoveryConfirmation == true;

    public string SizingSummaryText =>
        Readiness?.SizingPlan is { } p
            ? $"Proposed FAT32 Partition ('{SwbiBrand.PartitionVolumeLabel}'): {p.InstallerSizeDisplay} ({p.InstallerPartitionMb:N0} MB incl. {ByteFormatting.FormatGb(p.DriverReserveBytes)} PC Driver Reserve)  •  " +
              $"Old C: After Shrink: {p.OsAfterDisplay}  •  " +
              $"Split Chunks: {(p.EstimatedSwmCount > 0 ? $"{p.EstimatedSwmCount} .swm files" : "None (direct copy)")}"
            : "Validate an ISO in Step 2 to view exact SWBI-SETUP partition & driver reserve sizing.";

    public WorkflowOptionItem? SelectedWorkflowOption
    {
        get => _selectedWorkflowOption;
        set
        {
            // Ignore transient null pushed by WPF ComboBox during AvailableWorkflowOptions.Clear()
            if (value is not null)
            {
                SetProperty(ref _selectedWorkflowOption, value);
            }
        }
    }

    public bool ExportHostDrivers
    {
        get => _exportHostDrivers;
        set => SetProperty(ref _exportHostDrivers, value);
    }

    public bool InjectBootWimStorageDrivers
    {
        get => _injectBootWimStorageDrivers;
        set => SetProperty(ref _injectBootWimStorageDrivers, value);
    }

    public bool WriteEiCfgEditionUnlock
    {
        get => _writeEiCfgEditionUnlock;
        set => SetProperty(ref _writeEiCfgEditionUnlock, value);
    }

    public bool RunDrivePreShrinkOptimize
    {
        get => _runDrivePreShrinkOptimize;
        set => SetProperty(ref _runDrivePreShrinkOptimize, value);
    }

    public bool BypassWin11HardwareChecks
    {
        get => _bypassWin11HardwareChecks;
        set => SetProperty(ref _bypassWin11HardwareChecks, value);
    }

    public PreparationResult? LastPreparationResult
    {
        get => _preparationResult;
        private set
        {
            if (SetProperty(ref _preparationResult, value))
            {
                ReplaceCollection(LastPrepActions, value?.PlannedOrExecutedActions ?? Array.Empty<string>());
                OnPropertyChanged(nameof(LastPrepSummary));
                RebootIntoSetupCommand.NotifyCanExecuteChanged();
                NextStepCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string LastPrepSummary =>
        LastPreparationResult?.Summary ?? "Complete Step 4 first to prepare your SWBI self-bootable partition (or run a Dry-Run simulation).";

    public string PreparationGateHintText
    {
        get
        {
            if (IsBusy)
            {
                return "Operation currently in progress...";
            }

            if (SelectedIso is null || !SelectedIso.IsValid)
            {
                return "Action Needed: Go to Step 2 and select & validate any Official or Custom Windows 10/11 ISO first.";
            }

            if (Readiness is null)
            {
                return "Scanning PC readiness...";
            }

            if (!IsDryRunMode && EnvironmentSnapshot?.IsSimulated == true)
            {
                return "Virtual simulation scenarios run in Dry-Run Safety Mode — re-check 'Dry-Run Safety Mode' or uncheck Simulation Scenario.";
            }

            if (!IsDryRunMode && EnvironmentSnapshot?.IsElevated != true)
            {
                return "Live Hardware Mode requires running SWBI as Administrator (or enable Dry-Run Safety Mode).";
            }

            bool readyForMode = IsDryRunMode ? Readiness.CanSimulateDryRun : Readiness.CanProceedInLiveMode;
            if (!readyForMode)
            {
                return "Action Needed: Resolve blocking check(s) shown in Step 1 before starting preparation.";
            }

            if (!BuildConfirmations().AreMandatoryConfirmationsSatisfied(Readiness.RequiresBitLockerRecoveryConfirmation))
            {
                return "Check all mandatory safety confirmations above to enable the preparation button.";
            }

            return IsDryRunMode
                ? "Ready! Click 'Run Safe SWBI Dry-Run Simulation' below (0 disk writes)."
                : $"Ready! Click 'Build {SwbiBrand.PartitionVolumeLabel} & Arm SWBI Boot' below.";
        }
    }

    public string IsoPathInput
    {
        get => _isoPathInput;
        set
        {
            if (SetProperty(ref _isoPathInput, value))
            {
                ValidateIsoCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ComputeIsoSha256
    {
        get => _computeIsoSha256;
        set
        {
            if (SetProperty(ref _computeIsoSha256, value))
            {
                OnPropertyChanged(nameof(ShowIsoHashProgress));
            }
        }
    }

    public int IsoHashPercent
    {
        get => _isoHashPercent;
        private set => SetProperty(ref _isoHashPercent, value);
    }

    public bool ShowIsoHashProgress => ComputeIsoSha256 && IsBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(ShowIsoHashProgress));
                OnPropertyChanged(nameof(PreparationGateHintText));
                CancelOperationCommand.NotifyCanExecuteChanged();
                CleanupVm.ExternalBusy = value;
                CheckForUpdatesCommand.NotifyCanExecuteChanged();
                NotifyNavigationStateChanged();
            }
        }
    }

    public string BusyStatusText
    {
        get => _busyStatusText;
        private set => SetProperty(ref _busyStatusText, value);
    }

    public bool ConfirmExternalBackup
    {
        get => _confirmExternalBackup;
        set
        {
            if (SetProperty(ref _confirmExternalBackup, value))
            {
                OnPropertyChanged(nameof(PreparationGateHintText));
                StartPreparationCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ConfirmWipeOldPartition
    {
        get => _confirmWipeOldPartition;
        set
        {
            if (SetProperty(ref _confirmWipeOldPartition, value))
            {
                OnPropertyChanged(nameof(PreparationGateHintText));
                StartPreparationCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ConfirmPreserveInstaller
    {
        get => _confirmPreserveInstaller;
        set
        {
            if (SetProperty(ref _confirmPreserveInstaller, value))
            {
                OnPropertyChanged(nameof(PreparationGateHintText));
                StartPreparationCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ConfirmBitLockerRecoveryKey
    {
        get => _confirmBitLockerRecoveryKey;
        set
        {
            if (SetProperty(ref _confirmBitLockerRecoveryKey, value))
            {
                OnPropertyChanged(nameof(PreparationGateHintText));
                StartPreparationCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool AutoSuspendBitLocker
    {
        get => _autoSuspendBitLocker;
        set => SetProperty(ref _autoSuspendBitLocker, value);
    }

    public int PrepProgressPercent
    {
        get => _prepProgressPercent;
        private set => SetProperty(ref _prepProgressPercent, value);
    }

    public string PrepStepTitle
    {
        get => _prepStepTitle;
        private set => SetProperty(ref _prepStepTitle, value);
    }

    public string PrepStepDetail
    {
        get => _prepStepDetail;
        private set => SetProperty(ref _prepStepDetail, value);
    }

    public bool IsLogExpanded
    {
        get => _isLogExpanded;
        set => SetProperty(ref _isLogExpanded, value);
    }

    public string LatestLogLineText
    {
        get => _latestLogLineText;
        private set => SetProperty(ref _latestLogLineText, value);
    }

    public string LogCountBadgeText => $"{LogLines.Count} events";

    public MainViewModel(
        IDiskInspector diskInspector,
        IIsoInspector isoInspector,
        IBcdBootService bcdBootService,
        ReadinessEvaluator readinessEvaluator,
        SetupGuideGenerator guideGenerator,
        PreparationOrchestrator orchestrator,
        CleanupService cleanupService,
        ISafeLogger logger,
        bool startInCleanupMode = false,
        bool startInSimulationMode = false)
    {
        _diskInspector = diskInspector;
        _isoInspector = isoInspector;
        _bcdBootService = bcdBootService;
        _readinessEvaluator = readinessEvaluator;
        _guideGenerator = guideGenerator;
        _orchestrator = orchestrator;
        _logger = logger;

        _selectedScenario = SimulationScenarios[0];
        _isCleanupTabSelected = startInCleanupMode;

        var defaultOption = new WorkflowOptionItem(
            InstallWorkflowChoice.SelfBootableCleanInstall,
            "1. Self-Bootable Clean Install — Erase Old C: in Setup (Best for Clean Upgrade, Reinstall & Downgrade)");
        AvailableWorkflowOptions.Add(defaultOption);
        _selectedWorkflowOption = defaultOption;

        _logger.EntryWritten += OnLoggerEntryWritten;

        CleanupVm = new CleanupViewModel(
            cleanupService,
            () => IsDryRunMode ? ExecutionMode.DryRun : ExecutionMode.Live,
            () => UseSimulationScenario ? SelectedScenario : null);

        SelectWizardTabCommand = new RelayCommand(() => IsCleanupTabSelected = false);
        SelectCleanupTabCommand = new RelayCommand(() =>
        {
            IsCleanupTabSelected = true;
            _ = CleanupVm.InspectAsync();
        });

        PreviousStepCommand = new RelayCommand(
            () => CurrentWizardStep--,
            () => !IsBusy && CurrentWizardStep > 1);

        NextStepCommand = new RelayCommand(
            () => CurrentWizardStep++,
            CanMoveNextStep);

        JumpToStepCommand = new RelayCommand(param =>
        {
            if (!IsBusy && param is string s && int.TryParse(s, out int target))
            {
                CurrentWizardStep = target;
            }
        });

        RefreshSystemScanCommand = new AsyncRelayCommand(RefreshSystemScanAsync, () => !IsBusy);
        BrowseIsoCommand = new RelayCommand(BrowseIsoFile, () => !IsBusy);
        ValidateIsoCommand = new AsyncRelayCommand(ValidateSelectedIsoAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(IsoPathInput));
        ExportCheatSheetCommand = new RelayCommand(ExportCheatSheetToFile, () => _setupGuide is not null);
        StartPreparationCommand = new AsyncRelayCommand(RunPreparationAsync, CanStartPreparation);
        CancelOperationCommand = new RelayCommand(() => _activeOperationCts?.Cancel(), () => IsBusy && _activeOperationCts is not null);
        RebootIntoSetupCommand = new AsyncRelayCommand(ConfirmAndRebootAsync, CanRebootNow);
        CopyLogsCommand = new RelayCommand(CopyLogsToClipboard);
        OpenLogFolderCommand = new RelayCommand(OpenLogDirectory);
        ElevateAsAdminCommand = new RelayCommand(RelaunchElevatedFromUi, () => ShowElevateButton);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsBusy && !CleanupVm.IsBusy);
        CleanupVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CleanupViewModel.IsBusy)) CheckForUpdatesCommand.NotifyCanExecuteChanged();
        };

        if (startInSimulationMode || !OperatingSystem.IsWindows())
        {
            UseSimulationScenario = true;
        }
    }

    public async Task CheckForUpdatesAsync()
    {
        if (IsBusy || CleanupVm.IsBusy) return;
        using var cts = BeginBusyScope("Checking GitHub for a SWBI update...");
        IsUpdateRunning = true;
        UpdateProgressPercent = 0;
        UpdateStatusText = "Checking for updates...";
        UpdateApplyPlan? preparedPlan = null;
        try
        {
            StandaloneLauncherIdentity launcher = StandaloneLauncher.FromEnvironment();
            var updater = new GitHubReleaseUpdater();
            UpdateOffer? offer = await updater.CheckAsync(launcher.Flavor, cts.Token);
            if (offer is null)
            {
                UpdateStatusText = "SWBI is up to date.";
                BusyStatusText = UpdateStatusText;
                return;
            }

            bool confirmed = ShowInteractiveDialogs && MessageBox.Show(
                $"SWBI {offer.Tag} is available for {launcher.Flavor}. Download {offer.SizeBytes / 1024d / 1024d:F1} MiB, verify its SHA-256 checksum, then close and restart SWBI?\n\n" +
                "A backup of the current EXE will be kept beside it. The checksum detects download corruption but is not an independent publisher signature.",
                "SWBI — Update Available", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!confirmed)
            {
                UpdateStatusText = $"Update {offer.Tag} available; not installed.";
                BusyStatusText = UpdateStatusText;
                return;
            }

            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                if (!IsUpdateRunning) return;
                UpdateProgressPercent = p.Percent;
                UpdateStatusText = $"Downloading update: {p.Percent}%";
                BusyStatusText = UpdateStatusText;
            });
            preparedPlan = await updater.DownloadAsync(offer, launcher, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            StandaloneUpdateCoordinator.StartHelper(preparedPlan);
            preparedPlan = null; // The helper now owns the staged file.
            UpdateStatusText = "Verified update ready; restarting...";
            BusyStatusText = UpdateStatusText;
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Update cancelled; the original EXE was not replaced.";
            BusyStatusText = UpdateStatusText;
        }
        catch (Exception ex)
        {
            _logger.Error("Update", $"Update failed: {ex.Message}");
            UpdateStatusText = "Update failed; original EXE unchanged.";
            BusyStatusText = $"Update failed: {ex.Message}";
            if (ShowInteractiveDialogs)
                MessageBox.Show(BusyStatusText, "SWBI — Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (preparedPlan is not null)
            {
                try { File.Delete(preparedPlan.StagedPath); } catch { /* Leave the verified staging file for manual cleanup. */ }
            }
            IsUpdateRunning = false;
            EndBusyScope();
        }
    }

    public async Task InitializeAsync()
    {
        if (UseSimulationScenario)
        {
            ApplySelectedSimulation();
        }
        else
        {
            await RefreshSystemScanAsync();
        }

        if (IsCleanupTabSelected)
        {
            await CleanupVm.InspectAsync();
        }
    }

    public void ApplySelectedSimulation()
    {
        if (SelectedScenario is null)
        {
            return;
        }

        _logger.Info("Simulation", $"Loaded simulation scenario: {SelectedScenario.DisplayName}");
        EnvironmentSnapshot = SelectedScenario.Environment;
        SelectedIso = SelectedScenario.SampleIso;
        IsoPathInput = SelectedScenario.SampleIso.IsoPath;
        RebuildReadinessAndVisualMaps();

        if (IsCleanupTabSelected)
        {
            _ = CleanupVm.InspectAsync();
        }
    }

    public async Task RefreshSystemScanAsync()
    {
        if (UseSimulationScenario)
        {
            ApplySelectedSimulation();
            return;
        }

        using var cts = BeginBusyScope("Detecting PC hardware, drive type (HDD/SSD/NVMe/VMD), OS build, BitLocker & shrink space...");
        try
        {
            EnvironmentSnapshot = await _diskInspector.InspectEnvironmentAsync(cts.Token);
            RebuildReadinessAndVisualMaps();
            BusyStatusText = "Smart PC & drive inspection complete.";
        }
        catch (Exception ex)
        {
            _logger.Error("UI", $"System scan error: {ex.Message}");
            BusyStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            EndBusyScope();
        }
    }

    private void BrowseIsoFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Any Official or Custom Windows 10 / 11 ISO Image",
            Filter = "Windows ISO Image (*.iso)|*.iso|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            IsoPathInput = dialog.FileName;
            _ = ValidateSelectedIsoAsync();
        }
    }

    public async Task ValidateSelectedIsoAsync()
    {
        if (UseSimulationScenario &&
            SelectedScenario is not null &&
            string.Equals(IsoPathInput, SelectedScenario.SampleIso.IsoPath, StringComparison.OrdinalIgnoreCase))
        {
            SelectedIso = SelectedScenario.SampleIso;
            RebuildReadinessAndVisualMaps();
            return;
        }

        using var cts = BeginBusyScope($"Inspecting ISO '{System.IO.Path.GetFileName(IsoPathInput)}'...");
        IsoHashPercent = 0;
        try
        {
            var hashProgress = new Progress<int>(pct => IsoHashPercent = pct);
            SelectedIso = await _isoInspector.InspectIsoAsync(
                IsoPathInput,
                ComputeIsoSha256,
                hashProgress,
                cts.Token);

            RebuildReadinessAndVisualMaps();
            BusyStatusText = SelectedIso.IsValid
                ? $"Validated ISO ({SelectedIso.Editions.Count} edition(s), {Readiness?.Compatibility?.TransitionBadge})."
                : "ISO validation found errors.";
        }
        catch (OperationCanceledException)
        {
            BusyStatusText = "ISO validation cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Error("ISO", $"ISO inspection failed: {ex.Message}");
            BusyStatusText = $"ISO error: {ex.Message}";
        }
        finally
        {
            EndBusyScope();
        }
    }

    private void RebuildReadinessAndVisualMaps()
    {
        IsoEditions.Clear();
        if (SelectedIso is not null)
        {
            foreach (WimEditionInfo ed in SelectedIso.Editions)
            {
                IsoEditions.Add(ed);
            }
        }

        if (EnvironmentSnapshot is null)
        {
            return;
        }

        Readiness = _readinessEvaluator.Evaluate(EnvironmentSnapshot, SelectedIso);

        // Apply smart recommended hardware/driver options automatically on scan/ISO load
        if (Readiness.Compatibility is { } compat)
        {
            ExportHostDrivers = compat.RecommendedOptions.ExportHostStorageAndNetworkDrivers;
            InjectBootWimStorageDrivers = compat.RecommendedOptions.InjectBootCriticalStorageDriversIntoBootWim;
            WriteEiCfgEditionUnlock = compat.RecommendedOptions.WriteEiCfgToUnlockAllEditions;
            RunDrivePreShrinkOptimize = compat.RecommendedOptions.RunDriveAwarePreShrinkOptimization;
            BypassWin11HardwareChecks = compat.RecommendedOptions.BypassWin11HardwareChecks;

            InstallWorkflowChoice prevChoice = _selectedWorkflowOption?.Value ?? InstallWorkflowChoice.SelfBootableCleanInstall;
            AvailableWorkflowOptions.Clear();

            var cleanOpt = new WorkflowOptionItem(
                InstallWorkflowChoice.SelfBootableCleanInstall,
                "1. Self-Bootable Clean Install — Erase Old C: in Setup (Best for Clean Upgrade, Reinstall & Downgrade)");
            var winOldOpt = new WorkflowOptionItem(
                InstallWorkflowChoice.SelfBootablePreserveWindowsOld,
                "2. Self-Bootable Install Keeping C:\\Windows.old — Don't Format C: in Setup (Great for Downgrades)");

            AvailableWorkflowOptions.Add(cleanOpt);
            AvailableWorkflowOptions.Add(winOldOpt);

            if (compat.SupportsInPlaceKeepAppsUpgrade)
            {
                AvailableWorkflowOptions.Add(new WorkflowOptionItem(
                    InstallWorkflowChoice.InPlaceUpgradeKeepAppsAndFiles,
                    "3. In-Place Upgrade From Local Partition — Keep All Apps, Files & Settings"));
            }

            SelectedWorkflowOption = AvailableWorkflowOptions.FirstOrDefault(o => o.Value == prevChoice) ?? cleanOpt;
        }

        ReadinessChecks.Clear();
        foreach (SystemCheckItem item in Readiness.Checks)
        {
            ReadinessChecks.Add(item);
        }

        ReplaceCollection(BeforeDiskSegments, PartitionMapBuilder.BuildBeforeMap(EnvironmentSnapshot));
        ReplaceCollection(AfterPrepDiskSegments, PartitionMapBuilder.BuildAfterPrepMap(EnvironmentSnapshot, Readiness.SizingPlan));
        ReplaceCollection(SetupDeletedDiskSegments, PartitionMapBuilder.BuildSetupDeletedMap(EnvironmentSnapshot, Readiness.SizingPlan));

        SetupGuideRows.Clear();
        CriticalSetupRules.Clear();

        if (EnvironmentSnapshot.TargetDisk is not null && Readiness.SizingPlan is not null)
        {
            _setupGuide = _guideGenerator.Generate(EnvironmentSnapshot, Readiness.SizingPlan);
            foreach (SetupPartitionRow row in _setupGuide.Rows)
            {
                SetupGuideRows.Add(row);
            }

            foreach (string rule in _setupGuide.CriticalRules)
            {
                CriticalSetupRules.Add(rule);
            }
        }
        else
        {
            _setupGuide = null;
        }

        OnPropertyChanged(nameof(HasSetupGuide));
        OnPropertyChanged(nameof(PreparationGateHintText));
        ExportCheatSheetCommand.NotifyCanExecuteChanged();
    }

    public bool CanMoveNextStep() =>
        !IsBusy && CurrentWizardStep switch
        {
            1 => EnvironmentSnapshot is not null,
            2 => SelectedIso is { IsValid: true },
            3 => Readiness is not null && (IsDryRunMode ? Readiness.CanSimulateDryRun : Readiness.CanProceedInLiveMode),
            4 => LastPreparationResult is { Succeeded: true },
            _ => false
        };

    public bool CanStartPreparation()
    {
        if (IsBusy || Readiness is null)
        {
            return false;
        }

        var confirmations = BuildConfirmations();
        if (!confirmations.AreMandatoryConfirmationsSatisfied(Readiness.RequiresBitLockerRecoveryConfirmation))
        {
            return false;
        }

        return IsDryRunMode
            ? Readiness.CanSimulateDryRun
            : Readiness.CanProceedInLiveMode;
    }

    public async Task RunPreparationAsync()
    {
        if (Readiness is null)
        {
            return;
        }

        ExecutionMode mode = IsDryRunMode ? ExecutionMode.DryRun : ExecutionMode.Live;
        SmartTailoringOptions tailoring = BuildTailoringOptions();

        if (mode == ExecutionMode.Live && ShowInteractiveDialogs)
        {
            MessageBoxResult confirm = MessageBox.Show(
                $"{SwbiBrand.BrandedTitle.ToUpperInvariant()} — LIVE EXECUTION CONFIRMATION\n\n" +
                $"Target Drive: {EnvironmentSnapshot?.TargetDisk?.DriveKindDisplay} — Disk {EnvironmentSnapshot?.TargetDiskNumber} ({EnvironmentSnapshot?.TargetDisk?.FriendlyName})\n" +
                $"Transition Mode: {TransitionBadgeText} ({tailoring.WorkflowChoice})\n" +
                $"Shrink OS Partition ({EnvironmentSnapshot?.SystemDriveLetter}:) to: {Readiness.SizingPlan?.OsAfterDisplay}\n" +
                $"Create FAT32 '{SwbiBrand.PartitionVolumeLabel}' Partition: {Readiness.SizingPlan?.InstallerSizeDisplay}\n" +
                $"Temporary Boot Entry Name (UEFI or legacy BCD): {SwbiBrand.BootChooserEntryName}\n" +
                $"Windows Setup Driver Chooser Folder: {SwbiBrand.DriverChooserFolderName}\n" +
                $"Export PC Storage & Wi-Fi Drivers: {(tailoring.ExportHostStorageAndNetworkDrivers ? "Yes" : "No")}\n" +
                $"Inject Storage Drivers into boot.wim: {(tailoring.InjectBootCriticalStorageDriversIntoBootWim ? "Yes" : "No")}\n\n" +
                $"Have you verified your external backup and connected AC power?",
                $"{SwbiBrand.ShortName} — Confirm Smart Partition & Boot Preparation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        using var cts = BeginBusyScope(mode == ExecutionMode.DryRun
            ? "Running Smart SWBI Dry-Run preparation check..."
            : $"Preparing '{SwbiBrand.PartitionVolumeLabel}' self-bootable Windows installer partition...");

        var progress = new Progress<PreparationProgressUpdate>(u =>
        {
            PrepProgressPercent = u.Percent;
            PrepStepTitle = $"Step {u.StepIndex}/{u.TotalSteps}: {u.StepTitle}";
            PrepStepDetail = u.Detail;
        });

        bool rescanRequired = true;
        try
        {
            PreparationResult result = await _orchestrator.ExecuteAsync(
                Readiness,
                BuildConfirmations(),
                mode,
                progress,
                cts.Token,
                tailoring);

            LastPreparationResult = result;
            BusyStatusText = result.Summary;

            if (result.Succeeded)
            {
                rescanRequired = false;
                PrepProgressPercent = 100;
                CurrentWizardStep = 5;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is only observed at safe step boundaries, but earlier steps may already have been
            // applied. Tell the user that and re-scan so the wizard never shows the pre-cancel disk state.
            _logger.Warn("UI", "Preparation was cancelled; re-scanning to show the current disk state.");
            PrepStepTitle = "Cancelled";
            PrepStepDetail = "Preparation stopped at a safe boundary. Steps that already completed are still applied.";
            BusyStatusText = "Preparation cancelled — re-scanning the disk so the wizard reflects its real state.";
        }
        catch (Exception ex)
        {
            // Anything thrown outside the orchestrator's own error handling would otherwise surface only as a
            // generic dialog; keep the specific message and the recovery hint in the wizard.
            _logger.Error("UI", $"Preparation failed outside the orchestrator's error handling: {ex}");
            PrepStepTitle = "Preparation failed";
            PrepStepDetail = ex.Message;
            BusyStatusText = $"Preparation failed: {ex.Message}. Inspect the disk and the log before retrying.";
            if (ShowInteractiveDialogs)
            {
                MessageBox.Show(
                    $"Preparation failed unexpectedly:\n\n{ex.Message}\n\n" +
                    "Some steps may already have been applied. Re-scan the disk and check the log before retrying.\n\n" +
                    $"Log file:\n{_logger.LogFilePath ?? "C:\\ProgramData\\SWBI\\Work\\logs"}",
                    $"{SwbiBrand.ShortName} — Preparation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            EndBusyScope();
        }

        if (rescanRequired)
        {
            // Reflect whatever actually happened on disk (shrunk C:, created partition, armed boot entry)
            // instead of leaving the wizard showing the pre-cancellation state.
            await RefreshSystemScanAsync();
        }
    }

    private bool CanRebootNow() =>
        !IsBusy &&
        !IsDryRunMode &&
        LastPreparationResult is { Succeeded: true, Mode: ExecutionMode.Live };

    private async Task ConfirmAndRebootAsync()
    {
        if (ShowInteractiveDialogs)
        {
            MessageBoxResult answer = MessageBox.Show(
                $"Your PC will restart into '{SwbiBrand.BootChooserEntryName}' in 5 seconds.\n\n" +
                "REMEMBER IN WINDOWS SETUP:\n" +
                $"• Boot Chooser (if opened via F12/F8): '{SwbiBrand.BootChooserEntryName}'\n" +
                "• Delete or format ONLY your shrunk old Windows partition (or select it without formatting if keeping Windows.old).\n" +
                $"• DO NOT delete '{SwbiBrand.PartitionVolumeLabel}' or the EFI/System partition!\n" +
                $"• If prompted for drivers, browse to '{SwbiBrand.PartitionVolumeLabel}\\{SwbiBrand.DriverChooserFolderName}'.\n\n" +
                $"Restart into {SwbiBrand.BootChooserEntryName} now?",
                $"{SwbiBrand.ShortName} — Restart into Windows Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            await _bcdBootService.RebootSystemAsync(5);
        }
        catch (Exception ex)
        {
            if (ShowInteractiveDialogs)
            {
                MessageBox.Show($"Could not initiate restart automatically: {ex.Message}", "Restart Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public string? GetFormattedCheatSheetText() =>
        _setupGuide is null ? null : _guideGenerator.FormatPlainTextCheatSheet(_setupGuide);

    private void ExportCheatSheetToFile()
    {
        if (_setupGuide is null)
        {
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = $"Save {SwbiBrand.ShortName} Boot & Partition Cheat Sheet",
            FileName = $"SWBI-Setup-Partition-Guide-Disk{_setupGuide.TargetDiskNumber}.txt",
            Filter = "Text Document (*.txt)|*.txt"
        };

        if (saveDialog.ShowDialog() == true)
        {
            string text = _guideGenerator.FormatPlainTextCheatSheet(_setupGuide);
            File.WriteAllText(saveDialog.FileName, text, Encoding.UTF8);
            _logger.Info("Guide", $"Exported Setup Partition Cheat Sheet to '{saveDialog.FileName}'.");
            BusyStatusText = $"Saved Setup Partition Cheat Sheet to {saveDialog.FileName}";
        }
    }

    private void CopyLogsToClipboard()
    {
        try
        {
            string joined = string.Join(Environment.NewLine, LogLines);
            Clipboard.SetText(joined);
            BusyStatusText = "Copied redacted session log to clipboard.";
        }
        catch
        {
            // Ignore clipboard contention
        }
    }

    private void OpenLogDirectory()
    {
        string? dir = string.IsNullOrWhiteSpace(_logger.LogFilePath)
            ? null
            : System.IO.Path.GetDirectoryName(_logger.LogFilePath);

        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
    }

    private SmartTailoringOptions BuildTailoringOptions() =>
        new()
        {
            WorkflowChoice = SelectedWorkflowOption?.Value ?? InstallWorkflowChoice.SelfBootableCleanInstall,
            ExportHostStorageAndNetworkDrivers = ExportHostDrivers,
            InjectBootCriticalStorageDriversIntoBootWim = InjectBootWimStorageDrivers,
            WriteEiCfgToUnlockAllEditions = WriteEiCfgEditionUnlock,
            RunDriveAwarePreShrinkOptimization = RunDrivePreShrinkOptimize,
            BypassWin11HardwareChecks = BypassWin11HardwareChecks
        };

    private UserSafetyConfirmations BuildConfirmations() =>
        new()
        {
            HasVerifiedExternalBackup = ConfirmExternalBackup,
            UnderstandsCleanInstallWipesTargetPartition = ConfirmWipeOldPartition,
            UnderstandsNeverDeleteInstallerPartition = ConfirmPreserveInstaller,
            HasSavedBitLockerRecoveryKey = ConfirmBitLockerRecoveryKey,
            AutoSuspendBitLockerForNextReboot = AutoSuspendBitLocker
        };

    private CancellationTokenSource BeginBusyScope(string message)
    {
        _activeOperationCts?.Dispose();
        _activeOperationCts = new CancellationTokenSource();
        BusyStatusText = message;
        IsBusy = true;
        return _activeOperationCts;
    }

    private void EndBusyScope()
    {
        _activeOperationCts?.Dispose();
        _activeOperationCts = null;
        IsBusy = false;
    }

    private void NotifyNavigationStateChanged()
    {
        PreviousStepCommand.NotifyCanExecuteChanged();
        NextStepCommand.NotifyCanExecuteChanged();
        RefreshSystemScanCommand.NotifyCanExecuteChanged();
        BrowseIsoCommand.NotifyCanExecuteChanged();
        ValidateIsoCommand.NotifyCanExecuteChanged();
        StartPreparationCommand.NotifyCanExecuteChanged();
        RebootIntoSetupCommand.NotifyCanExecuteChanged();
    }

    private void OnLoggerEntryWritten(object? sender, SafeLogEntry e)
    {
        string formatted = $"{e.TimestampUtc:HH:mm:ss} [{e.Level}] [{e.Category}] {e.Message}";
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => AppendLogLine(formatted));
        }
        else
        {
            AppendLogLine(formatted);
        }
    }

    private void AppendLogLine(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > 500)
        {
            LogLines.RemoveAt(0);
        }

        LatestLogLineText = line;
        OnPropertyChanged(nameof(LogCountBadgeText));
    }

    private void RelaunchElevatedFromUi()
    {
        if (App.TryRelaunchSelfAsAdmin(Array.Empty<string>()))
        {
            Application.Current?.Shutdown(0);
        }
        else if (ShowInteractiveDialogs)
        {
            MessageBox.Show(
                "Windows UAC elevation could not be started (either UAC was declined or the Windows 'Application Information' service is disabled on this OS).\n\nTip: Right-click the SWBI executable and select 'Run as administrator'.",
                $"{SwbiBrand.ShortName} — Administrator Elevation",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (T item in items)
        {
            target.Add(item);
        }
    }
}
