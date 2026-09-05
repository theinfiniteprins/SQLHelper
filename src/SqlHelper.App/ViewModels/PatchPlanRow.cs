using CommunityToolkit.Mvvm.ComponentModel;
using SqlHelper.Core.Patching;

namespace SqlHelper.App.ViewModels;

/// <summary>One target's row in the patch plan/review grid.</summary>
public partial class PatchPlanRow : ObservableObject
{
    public required PatchAttempt Attempt { get; init; }

    [ObservableProperty]
    private bool isApproved;

    public string ClientName => Attempt.Target.ClientName;

    public string Environment => Attempt.Target.Environment.ToString();

    public string Outcome => Attempt.Outcome switch
    {
        PatchOutcome.Applied => "✓ Applied — exact match",
        PatchOutcome.NeedsReview => "△ Needs review",
        PatchOutcome.AlreadyCurrent => "· Already current",
        PatchOutcome.ManualRequired => "✕ Manual edit needed",
        PatchOutcome.ValidationFailed => "✕ Failed validation",
        _ => Attempt.Outcome.ToString(),
    };

    public string Tier => Attempt.HunkAttempts.Any(h => h.Tier == PatchTier.FuzzyAnchor && h.Applied)
        ? "Fuzzy"
        : Attempt.HunkAttempts.Any(h => h.Tier == PatchTier.ExactAnchor && h.Applied)
            ? "Exact"
            : "—";

    public string Summary => Attempt.Summary;

    /// <summary>Only a clean exact match or a fuzzy match that passed validation can ever be approved.</summary>
    public bool CanApprove => Attempt.Outcome is PatchOutcome.Applied or PatchOutcome.NeedsReview;

    public IReadOnlyList<DiffLine> Diff => DiffRenderer.Build(Attempt.OriginalBody, Attempt.PatchedBody ?? Attempt.OriginalBody);

    public IReadOnlyList<string> Warnings => Attempt.Validation?.Warnings ?? [];
}
