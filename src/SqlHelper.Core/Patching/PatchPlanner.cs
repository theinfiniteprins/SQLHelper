using SqlHelper.Core.Model;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Patching;

public enum PatchTier
{
    ExactAnchor,
    FuzzyAnchor,
}

public enum PatchOutcome
{
    /// <summary>Every hunk matched uniquely, in order. Ready to review and deploy.</summary>
    Applied,

    /// <summary>The target already contains the replacement text — nothing to do.</summary>
    AlreadyCurrent,

    /// <summary>A hunk needed the fuzzy tier, or the change size looks large — surfaced for a closer look before approval.</summary>
    NeedsReview,

    /// <summary>Neither tier found a confident match — this target's code differs too much here; needs a hand edit.</summary>
    ManualRequired,

    /// <summary>A tier produced text, but it failed validation (bad parse, wrong object, wrong kind) — never offered for approval.</summary>
    ValidationFailed,
}

public sealed record HunkAttempt(int Index, PatchTier? Tier, bool Applied, int MatchLine, string Detail);

public sealed record PatchAttempt(
    DatabaseTarget Target,
    PatchOutcome Outcome,
    string OriginalBody,
    string? PatchedBody,
    IReadOnlyList<HunkAttempt> HunkAttempts,
    PatchValidation? Validation,
    string Summary)
{
    /// <summary>Applied cleanly with every hunk at the exact tier, no size warning — the only tier that is ever auto-checked in bulk review.</summary>
    public bool IsCleanExactMatch =>
        Outcome == PatchOutcome.Applied
        && HunkAttempts.All(h => h.Tier == PatchTier.ExactAnchor)
        && Validation is { Warnings.Count: 0 };
}

/// <summary>
/// Runs one target through the patch pipeline: try every hunk at the exact tier; if any hunk
/// fails, retry the whole set at the fuzzy tier; if that still fails, or validation rejects the
/// result, the target is left untouched and flagged for a manual edit. Hunks are never applied
/// partially — a target either gets every hunk or none of them.
/// </summary>
public static class PatchPlanner
{
    public static PatchAttempt Plan(DatabaseTarget target, ProgrammableObject currentModule, PatchDefinition patch)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(currentModule);
        ArgumentNullException.ThrowIfNull(patch);

        string original = currentModule.Definition;

        if (IsAlreadyCurrent(original, patch))
        {
            return new PatchAttempt(target, PatchOutcome.AlreadyCurrent, original, null, [], null,
                "Already has this change.");
        }

        AttemptRun exact = RunTier(original, patch, PatchTier.ExactAnchor);
        AttemptRun chosen = exact;
        bool usedFuzzy = false;

        if (!exact.AllApplied)
        {
            AttemptRun fuzzy = RunTier(original, patch, PatchTier.FuzzyAnchor);
            if (fuzzy.AllApplied)
            {
                chosen = fuzzy;
                usedFuzzy = true;
            }
            else
            {
                // Prefer showing whichever tier got further, for diagnostics.
                chosen = fuzzy.Attempts.Count(a => a.Applied) >= exact.Attempts.Count(a => a.Applied) ? fuzzy : exact;
            }
        }

        if (!chosen.AllApplied || chosen.FinalText is null)
        {
            string why = chosen.Attempts.FirstOrDefault(a => !a.Applied)?.Detail
                ?? "This client's code differs too much here to place the change safely.";
            return new PatchAttempt(target, PatchOutcome.ManualRequired, original, null, chosen.Attempts, null, why);
        }

        int totalHunkLines = patch.Hunks.Sum(h => h.AnchorText.Split('\n').Length);
        PatchValidation validation = PatchValidator.Validate(
            original, chosen.FinalText, patch.TargetObject, currentModule.Kind, totalHunkLines);

        if (!validation.Ok)
        {
            return new PatchAttempt(target, PatchOutcome.ValidationFailed, original, chosen.FinalText, chosen.Attempts,
                validation, string.Join(" ", validation.Errors));
        }

        bool needsReview = usedFuzzy || validation.Warnings.Count > 0;
        string summary = needsReview
            ? $"Applied via {(usedFuzzy ? "the fuzzy tier" : "an exact match")}; review before approving."
            : "Applied — exact match on every hunk.";

        return new PatchAttempt(
            target,
            needsReview ? PatchOutcome.NeedsReview : PatchOutcome.Applied,
            original,
            chosen.FinalText,
            chosen.Attempts,
            validation,
            summary);
    }

    private static bool IsAlreadyCurrent(string original, PatchDefinition patch)
    {
        // Heuristic: none of the anchors are present, but every replacement already is —
        // the target has moved on to (at least) this change already.
        bool anyAnchorPresent = patch.Hunks.Any(h => AnchorMatcher.TryApply(original, h.AnchorText, h.ReplacementText).Status != AnchorMatchStatus.NotFound);
        if (anyAnchorPresent)
        {
            return false;
        }

        return patch.Hunks.All(h => ContainsNormalized(original, h.ReplacementText));
    }

    private static bool ContainsNormalized(string haystack, string needle)
    {
        static string[] Lines(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        string[] hay = Lines(haystack);
        string[] pin = Lines(needle);
        if (pin.Length == 0)
        {
            return true;
        }

        for (int i = 0; i + pin.Length <= hay.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pin.Length; j++)
            {
                if (hay[i + j] != pin[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static AttemptRun RunTier(string original, PatchDefinition patch, PatchTier tier)
    {
        string working = original;
        var attempts = new List<HunkAttempt>();
        bool allApplied = true;

        for (int i = 0; i < patch.Hunks.Count; i++)
        {
            PatchHunk hunk = patch.Hunks[i];

            if (tier == PatchTier.ExactAnchor)
            {
                AnchorMatchResult result = AnchorMatcher.TryApply(working, hunk.AnchorText, hunk.ReplacementText);
                if (result.IsUnique && result.PatchedText is not null)
                {
                    working = result.PatchedText;
                    attempts.Add(new HunkAttempt(i, tier, true, result.FirstMatchLine, "Exact match."));
                }
                else
                {
                    allApplied = false;
                    attempts.Add(new HunkAttempt(i, tier, false, -1, Describe(result)));
                }
            }
            else
            {
                FuzzyPatchResult result = FuzzyPatcher.TryApply(working, hunk.AnchorText, hunk.ReplacementText);
                if (result.Applied && result.PatchedText is not null)
                {
                    working = result.PatchedText;
                    attempts.Add(new HunkAttempt(i, tier, true, result.MatchLine, result.Reason));
                }
                else
                {
                    allApplied = false;
                    attempts.Add(new HunkAttempt(i, tier, false, result.MatchLine, result.Reason));
                }
            }
        }

        return new AttemptRun(allApplied, allApplied ? working : null, attempts);
    }

    private static string Describe(AnchorMatchResult result) => result.Status switch
    {
        AnchorMatchStatus.NotFound => "Anchor text not found.",
        AnchorMatchStatus.Ambiguous => $"Anchor text matched {result.MatchCount} places — not unique.",
        _ => "No match.",
    };

    private sealed record AttemptRun(bool AllApplied, string? FinalText, List<HunkAttempt> Attempts);
}
