using System.Collections.ObjectModel;
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

    private CleanupInspectionResult? _inspection;
    private bool _extendOsIfAdjacent = true;
    private bool _confirmedIndependentBoot;
    private string _statusMessage = $"Click 'Inspect Post-Install State' to check for temporary '{SwbiBrand.PartitionVolumeLabel}' partitions and '{SwbiBrand.BootChooserEntryName}' boot entries.";
    private string _statusDetail = string.Empty;
    private CleanupExecutionResult? _lastResult;
    private bool _isBusy;
    private bool _externalBusy;

    public ObservableCollection<SystemCheckItem> SafetyChecks { get; } = new();

    public AsyncRelayCommand InspectCommand { get; }
    public AsyncRelayCommand ExecuteCleanupCommand { get; }

    public CleanupInspectionResult? Inspection
    {
        get => _inspection;
        private set
        {
            if (SetProperty(ref _inspection, value))
            {
                OnPropertyChanged(nameof(HasCandidatePartition));
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
                ExecuteCleanupCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasCandidatePartition => Inspection?.CandidatePartition is not null;

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

    public CleanupViewModel(
        CleanupService cleanupService,
        Func<ExecutionMode> getExecutionMode,
        Func<SimulationScenario?> getActiveSimulation)
    {
        _cleanupService = cleanupService;
        _getExecutionMode = getExecutionMode;
        _getActiveSimulation = getActiveSimulation;

        InspectCommand = new AsyncRelayCommand(InspectAsync, () => !IsBusy && !ExternalBusy);
        ExecuteCleanupCommand = new AsyncRelayCommand(ExecuteCleanupAsync, CanRunCleanup);
    }

    public async Task InspectAsync()
    {
        if (IsBusy || ExternalBusy) return;
        IsBusy = true;
        try
        {
            LastResult = null;
            StatusDetail = string.Empty;
            StatusMessage = "Inspecting active boot configuration, UEFI/BCD entries, GPT/MBR partition identities, and adjacency...";

            SimulationScenario? sim = _getActiveSimulation();
            CleanupInspectionResult result = await _cleanupService.InspectAsync(
                providedSnapshot: sim?.Environment,
                simulatedManifest: sim?.CleanupManifest);

            SafetyChecks.Clear();
            foreach (SystemCheckItem item in result.SafetyChecks)
            {
                SafetyChecks.Add(item);
            }

            Inspection = result;
            ExtendOsIfAdjacent = result.Adjacency?.CanExtendOsPartitionDirectly ?? false;

            StatusMessage = result.State switch
            {
                CleanupSafetyState.ReadyToClean =>
                    $"Ready to remove temporary {SwbiBrand.ShortName} installer resources safely.",
                CleanupSafetyState.NoInstallerArtifactsFound =>
                    $"Clean! No temporary '{SwbiBrand.PartitionVolumeLabel}' partition or '{SwbiBrand.BootChooserEntryName}' boot entry exists on this system.",
                CleanupSafetyState.BlockedActiveDependency =>
                    "Blocked: Current session still depends on a ramdisk or SWBI installer volume.",
                CleanupSafetyState.BlockedAmbiguousPartition =>
                    $"Blocked: Candidate partition lacks a verified {SwbiBrand.ManifestFileName} matching its GPT/MBR partition identity.",
                _ => "Inspection complete."
            };
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cleanup inspection was cancelled.";
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

    public bool CanRunCleanup() =>
        !IsBusy && !ExternalBusy && Inspection is { CanExecuteCleanup: true } && ConfirmedIndependentBoot;

    public async Task ExecuteCleanupAsync()
    {
        if (Inspection is null || IsBusy || ExternalBusy) return;
        IsBusy = true;
        try
        {
            ExecutionMode mode = _getExecutionMode();
            StatusMessage = mode == ExecutionMode.DryRun
                ? $"Simulating {SwbiBrand.ShortName} post-install cleanup in Dry-Run mode..."
                : $"Removing '{SwbiBrand.BootChooserEntryName}' boot entries and '{SwbiBrand.PartitionVolumeLabel}' partition...";

            CleanupExecutionResult execResult = await _cleanupService.ExecuteCleanupAsync(
                Inspection,
                ExtendOsIfAdjacent,
                mode);

            LastResult = execResult;
            StatusMessage = execResult.Summary;
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
