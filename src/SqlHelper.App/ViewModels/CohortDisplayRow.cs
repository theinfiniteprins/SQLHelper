using SqlHelper.App.Services;
using SqlHelper.Core.Patching;

namespace SqlHelper.App.ViewModels;

/// <summary>One version cluster, for the "which clients are on what" summary before any patch is authored.</summary>
public sealed class CohortDisplayRow
{
    public required string Label { get; init; }

    public required int Count { get; init; }

    public required string Members { get; init; }

    public bool Degraded { get; init; }

    public bool IsFailure { get; init; }

    /// <summary>For a group that couldn't be read: what went wrong, in terms the operator can act on.</summary>
    public string? Explanation { get; init; }

    public static CohortDisplayRow From(Cohort cohort) => new()
    {
        Label = cohort.Label,
        Count = cohort.Count,
        Members = string.Join(", ", cohort.Members.Select(m => m.ClientName)),
        Degraded = cohort.Degraded,
        IsFailure = cohort.IsFailure,
        Explanation = cohort.IsFailure ? ConnectionErrorHelp.Explain(cohort.Error) : null,
    };
}
