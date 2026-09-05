using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlHelper.App.Services;
using SqlHelper.Core.Model;
using SqlHelper.Core.Orchestration;
using SqlHelper.Core.Patching;
using SqlHelper.Core.Scripting;

namespace SqlHelper.App.ViewModels;

/// <summary>
/// The targeted-patch workflow: cluster clients into their handful of real versions, author one
/// change from a before/after reference pair, plan it against every selected database (exact tier,
/// then fuzzy, then a flag for a manual edit), review each result as a diff, approve, and deploy —
/// with the same backup-and-rollback pipeline as a full replace.
/// </summary>
public partial class PatchViewModel : ObservableObject
{
    private readonly AppSession _session;

    public TargetPickerViewModel Picker { get; }

    public ObservableCollection<CohortDisplayRow> Cohorts { get; } = [];

    public ObservableCollection<PatchPlanRow> PlanRows { get; } = [];

    public ObservableCollection<DeployResultRow> ApplyResults { get; } = [];

    [ObservableProperty]
    private string objectNameText = string.Empty;

    [ObservableProperty]
    private string beforeScript = string.Empty;

    [ObservableProperty]
    private string afterScript = string.Empty;

    [ObservableProperty]
    private string changeTitle = string.Empty;

    [ObservableProperty]
    private string? ticket;

    [ObservableProperty]
    private string backupRoot;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    /// <summary>The change itself: reference before vs reference after.</summary>
    public DiffReviewViewModel PatchPreview { get; } = new();

    /// <summary>What would change on the client currently selected in the plan list.</summary>
    public DiffReviewViewModel SelectedDiff { get; } = new();

    [ObservableProperty]
    private bool hasPatchPreview;

    [ObservableProperty]
    private PatchPlanRow? selectedPlanRow;

    private PatchDefinition? _builtPatch;
    private ObjectName? _objectName;

    public int ApprovedCount => PlanRows.Count(r => r.IsApproved);

    public PatchViewModel(AppSession session)
    {
        _session = session;
        Picker = new TargetPickerViewModel(session);
        backupRoot = session.Paths.DefaultBackupRoot;
    }

    partial void OnSelectedPlanRowChanged(PatchPlanRow? value)
    {
        if (value is null)
        {
            SelectedDiff.SetContent(string.Empty, string.Empty);
            return;
        }

        SelectedDiff.SetContent(
            value.Attempt.OriginalBody,
            value.Attempt.PatchedBody ?? value.Attempt.OriginalBody,
            $"{value.ClientName} — what would change");
    }

    [RelayCommand]
    private async Task AnalyzeCohortsAsync()
    {
        if (!TryParseObjectName(out ObjectName name))
        {
            return;
        }

        IReadOnlyList<DatabaseTarget> targets = Picker.SelectedTargets;
        if (targets.Count == 0)
        {
            DialogService.Warn("Pick at least one database first.");
            return;
        }

        IsBusy = true;
        StatusMessage = $"Reading {name.Plain} from {targets.Count} database(s)…";
        try
        {
            IReadOnlyList<ObjectCapture> captures = await _session.Engine.CaptureForCohortAnalysisAsync(targets, name);
            CohortReport report = CohortAnalyzer.Cluster(captures);

            Cohorts.Clear();
            foreach (Cohort cohort in report.Cohorts)
            {
                Cohorts.Add(CohortDisplayRow.From(cohort));
            }

            StatusMessage = report.AllIdentical
                ? "Every selected database already has the same definition."
                : $"{report.Cohorts.Count} distinct version(s) found across {targets.Count} database(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BuildPatch()
    {
        if (!TryParseObjectName(out _))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(BeforeScript) || string.IsNullOrWhiteSpace(AfterScript))
        {
            DialogService.Warn("Paste the reference procedure as it was, and as you want it to be.");
            return;
        }

        PatchDefinitionResult result = PatchDefinitionFactory.FromReferenceDiff(
            string.IsNullOrWhiteSpace(ChangeTitle) ? "patch" : ChangeTitle, BeforeScript, AfterScript);

        if (!result.Ok)
        {
            DialogService.Error(result.Error ?? "Could not build a patch from those two scripts.");
            return;
        }

        _builtPatch = result.Patch;
        PatchPreview.SetContent(BeforeScript, AfterScript, "The change you are about to apply");
        HasPatchPreview = true;
        PlanRows.Clear();
        ApplyResults.Clear();
        StatusMessage = $"Patch built: {_builtPatch!.Hunks.Count} change block(s). Now plan it against the selected databases.";
    }

    [RelayCommand]
    private async Task PlanPatchAsync()
    {
        if (_builtPatch is null || _objectName is null)
        {
            DialogService.Warn("Build the patch first.");
            return;
        }

        IReadOnlyList<DatabaseTarget> targets = Picker.SelectedTargets;
        if (targets.Count == 0)
        {
            DialogService.Warn("Pick at least one database.");
            return;
        }

        IsBusy = true;
        StatusMessage = $"Planning against {targets.Count} database(s)…";
        try
        {
            IReadOnlyList<PatchAttempt> attempts = await _session.Engine.PlanPatchAsync(targets, _objectName, _builtPatch);

            PlanRows.Clear();
            foreach (PatchAttempt attempt in attempts)
            {
                var row = new PatchPlanRow { Attempt = attempt, IsApproved = attempt.Outcome == PatchOutcome.Applied };
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PatchPlanRow.IsApproved))
                    {
                        OnPropertyChanged(nameof(ApprovedCount));
                    }
                };
                PlanRows.Add(row);
            }

            SelectedPlanRow = PlanRows.FirstOrDefault();
            int clean = attempts.Count(a => a.Outcome == PatchOutcome.Applied);
            int review = attempts.Count(a => a.Outcome == PatchOutcome.NeedsReview);
            int manual = attempts.Count(a => a.Outcome is PatchOutcome.ManualRequired or PatchOutcome.ValidationFailed);
            int current = attempts.Count(a => a.Outcome == PatchOutcome.AlreadyCurrent);
            StatusMessage = $"{clean} clean, {review} need review, {manual} need a manual edit, {current} already current.";
            OnPropertyChanged(nameof(ApprovedCount));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ApproveAllClean()
    {
        foreach (PatchPlanRow row in PlanRows.Where(r => r.Attempt.Outcome == PatchOutcome.Applied))
        {
            row.IsApproved = true;
        }
    }

    [RelayCommand]
    private async Task ApplyApprovedAsync()
    {
        List<PatchAttempt> approved = [.. PlanRows.Where(r => r.IsApproved && r.CanApprove).Select(r => r.Attempt)];
        if (approved.Count == 0)
        {
            DialogService.Warn("Nothing is approved yet.");
            return;
        }

        bool touchesProduction = approved.Any(a => a.Target.Environment == ServerEnvironment.Production);
        if (touchesProduction)
        {
            bool confirmed = DialogService.ConfirmProductionAction(
                $"Apply this patch to {approved.Count(a => a.Target.Environment == ServerEnvironment.Production)} production database(s). " +
                "Each target's current definition is backed up first, with a rollback script.");
            if (!confirmed)
            {
                return;
            }
        }

        IsBusy = true;
        StatusMessage = "Applying…";
        try
        {
            string description = string.IsNullOrWhiteSpace(ChangeTitle) ? "(no description)" : ChangeTitle.Trim();
            DeployResult result = await _session.Engine.ApplyPatchAsync(approved, _objectName!, BackupRoot, description, Ticket);
            ApplyResults.Clear();
            foreach (DeployTargetResult r in result.Results)
            {
                ApplyResults.Add(DeployResultRow.From(r));
            }

            StatusMessage = $"{result.SucceededCount}/{result.Results.Count} succeeded. Backup: {result.BackupFolder}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryParseObjectName(out ObjectName name)
    {
        if (ObjectName.TryParse(ObjectNameText, out ObjectName? parsed))
        {
            name = parsed!;
            _objectName = parsed;
            return true;
        }

        name = new ObjectName("dbo", string.Empty);
        DialogService.Warn("Enter a valid object name, e.g. dbo.usp_GetOrders.");
        return false;
    }
}
