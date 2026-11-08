using System.Collections.ObjectModel;
using System.Windows;
using UsbFreeInstaller.App.Infrastructure;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Simulation;

namespace UsbFreeInstaller.App.ViewModels;

public sealed class CleanupViewModel : ObservableObject
{
    private readonly CleanupService _cleanupService;
    private readonly Func<ExecutionMode> _getExecutionMode;
    private readonly Func<SimulationScenario?> _getActiveSimulation;
    private readonly IReadOnlyBootRepairDiagnostics _readOnlyBootDiagnostics;
    private readonly Action<string> _copyBootReport;

    private CleanupInspectionResult? _inspection;
    private bool _extendOsIfAdjacent = true;
    private bool _confirmedIndependentBoot;
    private string _statusMessage = $"Click 'Inspect SWBI State' to check for an orphaned boot entry or a temporary '{SwbiBrand.PartitionVolumeLabel}' partition. Inspection makes no changes.";
    private string _statusDetail = string.Empty;
    private CleanupExecutionResult? _lastResult;
    private string _bootRepairReport = string.Empty;
    private bool _isBusy;
    private bool _externalBusy;

    public ObservableCollection<SystemCheckItem> SafetyChecks { get; } = new();

    public AsyncRelayCommand InspectCommand { get; }
    public AsyncRelayCommand DiagnoseBootCommand { get; }
    public RelayCommand CopyBootReportCommand { get; }
    public AsyncRelayCommand ExecuteCleanupCommand { get; }

    public CleanupInspectionResult? Inspection
    {
        get => _inspection;
        private set
        {
            if (SetProperty(ref _inspection, value))
            {
                OnPropertyChanged(nameof(HasCandidatePartition));
                OnPropertyChanged(nameof(CleanupActionLabel));
                OnPropertyChanged(nameof(CanExtendOsDirectly));
                OnPropertyChanged(nameof(AdjacencySummary));
                ExecuteCleanupCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                InspectCommand.NotifyCanExecuteChanged();
                DiagnoseBootCommand.NotifyCanExecuteChanged();
                CopyBootReportCommand.NotifyCanExecuteChanged();
                ExecuteCleanupCommand.NotifyCanExecuteChanged();
            }
        }
    }

    internal bool ExternalBusy
    {
        get => _externalBusy;
        set
        {
            if (SetProperty(ref _externalBusy, value))
            {
                InspectCommand.NotifyCanExecuteChanged();
                DiagnoseBootCommand.NotifyCanExecuteChanged();
                CopyBootReportCommand.NotifyCanExecuteChanged();
                ExecuteCleanupCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasCandidatePartition => Inspection?.CandidatePartition is not null;

    public string CleanupActionLabel => Inspection switch
    {
        { CanExecuteCleanup: true, CandidatePartition: null } => "Repair Verified SWBI Boot Entries (Boot Only)",
        { State: CleanupSafetyState.BlockedAmbiguousPartition, CandidatePartition: null } =>
            "Boot Repair Blocked — Review Read-Only Report",
        _ => "Remove SWBI Boot Entries & Reclaim Partition"
    };

    public bool CanExtendOsDirectly => Inspection?.Adjacency is { CanExtendOsPartitionDirectly: true };

    public string AdjacencySummary =>
        Inspection?.Adjacency?.Explanation ??
        $"No temporary '{SwbiBrand.PartitionVolumeLabel}' installer partition detected yet.";

    public bool ExtendOsIfAdjacent
    {
        get => _extendOsIfAdjacent;
        set => SetProperty(ref _extendOsIfAdjacent, value);
    }

    public bool ConfirmedIndependentBoot
    {
        get => _confirmedIndependentBoot;
        set
        {
            if (SetProperty(ref _confirmedIndependentBoot, value))
            {
                ExecuteCleanupCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Secondary line explaining a failure and what to do next (empty when nothing failed).</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public CleanupExecutionResult? LastResult
    {
        get => _lastResult;
        private set
        {
            if (SetProperty(ref _lastResult, value))
            {
                OnPropertyChanged(nameof(LastCleanupSummary));
            }
        }
    }

    public string? LastCleanupSummary => LastResult?.Summary;

    public string BootRepairReport
    {
        get => _bootRepairReport;
        private set
        {
            if (SetProperty(ref _bootRepairReport, value))
            {
                OnPropertyChanged(nameof(HasBootRepairReport));
                CopyBootReportCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasBootRepairReport => !string.IsNullOrWhiteSpace(BootRepairReport);

    public CleanupViewModel(
        CleanupService cleanupService,
        Func<ExecutionMode> getExecutionMode,
        Func<SimulationScenario?> getActiveSimulation,
        IReadOnlyBootRepairDiagnostics? readOnlyBootDiagnostics = null,
        Action<string>? copyBootReport = null)
    {
        _cleanupService = cleanupService;
        _getExecutionMode = getExecutionMode;
        _getActiveSimulation = getActiveSimulation;
        _readOnlyBootDiagnostics = readOnlyBootDiagnostics ?? new WindowsReadOnlyBootRepairDiagnostics();
        _copyBootReport = copyBootReport ?? Clipboard.SetText;

        InspectCommand = new AsyncRelayCommand(InspectAsync, () => !IsBusy && !ExternalBusy);
        DiagnoseBootCommand = new AsyncRelayCommand(DiagnoseBootAsync, () => !IsBusy && !ExternalBusy);
        CopyBootReportCommand = new RelayCommand(CopyBootReport, () => HasBootRepairReport && !IsBusy && !ExternalBusy);
        ExecuteCleanupCommand = new AsyncRelayCommand(ExecuteCleanupAsync, CanRunCleanup);
    }

    public Task InspectAsync() => InspectAndMaybeDiagnoseAsync(captureReport: false);

    /// <summary>
    /// First re-inspect the current machine, then collect raw read-only BCD evidence.
    /// This is always available, even when a firmware-view GUID blocks live cleanup.
    /// </summary>
    public Task DiagnoseBootAsync() => InspectAndMaybeDiagnoseAsync(captureReport: true);

    private async Task InspectAndMaybeDiagnoseAsync(bool captureReport)
    {
        if (IsBusy || ExternalBusy) return;
        IsBusy = true;
        try
        {
            Inspection = null; // Stale inspection/consent cannot authorize a later repair.
            LastResult = null;
            BootRepairReport = string.Empty;
            SafetyChecks.Clear();
            ConfirmedIndependentBoot = false;
            StatusDetail = string.Empty;
            StatusMessage = "Inspecting active boot configuration, UEFI/BCD entries, GPT/MBR partition identities, and adjacency...";

            SimulationScenario? sim = _getActiveSimulation();
            CleanupInspectionResult result = await _cleanupService.InspectAsync(
                providedSnapshot: sim?.Environment,
                simulatedManifest: sim?.CleanupManifest);

            foreach (SystemCheckItem item in result.SafetyChecks)
                SafetyChecks.Add(item);

            Inspection = result;
            ExtendOsIfAdjacent = result.Adjacency?.CanExtendOsPartitionDirectly ?? false;
            StatusMessage = result.State switch
            {
                CleanupSafetyState.ReadyToClean when result.CandidatePartition is null =>
                    "Boot-only repair available for verified SWBI entries after review. No installer partition was found; C: will not be changed.",
                CleanupSafetyState.ReadyToClean =>
                    $"Ready to remove temporary {SwbiBrand.ShortName} installer resources after review.",
                CleanupSafetyState.NoInstallerArtifactsFound =>
                    $"Clean! No temporary '{SwbiBrand.PartitionVolumeLabel}' partition or '{SwbiBrand.BootChooserEntryName}' boot entry exists on this system.",
                CleanupSafetyState.BlockedActiveDependency =>
                    "Blocked: Cannot verify independent boot or inspect the active BCD. Read the safety checks before continuing.",
                CleanupSafetyState.BlockedAmbiguousPartition when result.SafetyChecks.Any(c => c.Id == "cleanup-firmware-mirror") =>
                    "Blocked: An SWBI-named BCD firmware-view GUID may mirror a UEFI slot; diagnose its device/path before any cleanup. This does NOT establish corruption.",
                CleanupSafetyState.BlockedAmbiguousPartition when result.SafetyChecks.Any(c => c.Id == "cleanup-firmware-ownership") =>
                    "Blocked: The SWBI-named UEFI entry has an unverified EFI path or slot; do not remove it by name alone.",
                CleanupSafetyState.BlockedAmbiguousPartition =>
                    $"Blocked: Candidate partition lacks a verified {SwbiBrand.ManifestFileName} matching its GPT/MBR partition identity.",
                _ => "Inspection complete."
            };

            if (captureReport)
            {
                StatusDetail = "Collecting a read-only boot report; this will not repair or delete anything...";
                try
                {
                    BootRepairReport = sim is null
                        ? await _readOnlyBootDiagnostics.CaptureAsync(result)
                        : $"SWBI BOOT REPAIR ASSESSMENT — SIMULATION ONLY\nState: {result.State}\nNo live Windows BCD, firmware, or partitions were inspected or changed.";
                    StatusDetail = result.CanExecuteCleanup
                        ? "Read-only report ready. After reviewing it, explicitly confirm independent boot to enable verified SWBI cleanup below."
                        : "Read-only report ready. Cleanup remains blocked; copy the report for review. Do not delete a BCD firmware GUID by name.";
                }
                catch (Exception ex)
                {
                    BootRepairReport = $"Read-only boot report could not be collected ({ex.GetType().Name}: {ex.Message}). No boot settings were changed.";
                    StatusDetail = "Read-only diagnosis failed; cleanup safety checks remain in effect. No repair was attempted.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            Inspection = null;
            SafetyChecks.Clear();
            StatusMessage = "Cleanup inspection was cancelled; no boot settings were changed.";
        }
        catch (Exception ex)
        {
            Inspection = null;
            SafetyChecks.Clear();
            StatusMessage = $"Cleanup inspection failed: {ex.Message}";
            StatusDetail = $"Nothing was modified. Re-run the inspection, or check the log for details. ({ex.GetType().Name})";
        }
        finally { IsBusy = false; }
    }

    private void CopyBootReport()
    {
        if (!HasBootRepairReport || IsBusy || ExternalBusy) return;
        try
        {
            _copyBootReport(BootRepairReport);
            StatusDetail = "Copied read-only boot report. Review it for private device identifiers before sharing.";
        }
        catch (Exception)
        {
            StatusDetail = "Clipboard is unavailable; select the read-only report text and copy it manually.";
        }
    }

    public bool CanRunCleanup() =>
        !IsBusy && !ExternalBusy && Inspection is { CanExecuteCleanup: true } && ConfirmedIndependentBoot;

    public async Task ExecuteCleanupAsync()
    {
        if (!CanRunCleanup() || Inspection is null) return;
        IsBusy = true;
        try
        {
            BootRepairReport = string.Empty; // Any prior report is stale once live state may change.
            ExecutionMode mode = _getExecutionMode();
            StatusMessage = mode == ExecutionMode.DryRun
                ? $"Simulating {SwbiBrand.ShortName} post-install cleanup in Dry-Run mode..."
                : Inspection.CandidatePartition is null
                    ? "Backing up the active BCD and removing recognized SWBI boot entries only; no partition will be changed..."
                    : $"Removing '{SwbiBrand.BootChooserEntryName}' boot entries and '{SwbiBrand.PartitionVolumeLabel}' partition...";

            CleanupExecutionResult execResult = await _cleanupService.ExecuteCleanupAsync(
                Inspection,
                ExtendOsIfAdjacent,
                mode);

            LastResult = execResult;
            StatusMessage = execResult.Summary;
            if (mode == ExecutionMode.Live)
            {
                // The inspected partition number/boot identities may now be stale even when an
                // operation partially failed. Require a fresh scan before another disk mutation.
                Inspection = null;
                ConfirmedIndependentBoot = false;
                SafetyChecks.Clear();
                StatusDetail = execResult.Succeeded
                    ? "Re-inspect the boot and disk state before preparing or cleaning anything else."
                    : "Re-inspect to see what remains; do not retry from the old inspection. The flags in the result show what already completed.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cleanup was cancelled.";
            StatusDetail = "Re-inspect the disk before retrying; a cancelled disk operation may still have completed.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cleanup failed: {ex.Message}";
            StatusDetail = "Re-inspect the disk to see what was already removed. Some changes may have been applied.";
        }
        finally { IsBusy = false; }
    }
}
