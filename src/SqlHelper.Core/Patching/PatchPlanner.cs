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
/// Runs one target through the patch pipeline. Hunks are applied in order; each is tried at the
/// exact tier first and, only if that cannot place it, at the tolerant tier. Each hunk must land
/// below the one before it — they come from one diff of one procedure, so they cannot legitimately
/// appear in a different order. If any hunk cannot be placed, or validation rejects the result,
/// the target is left untouched and flagged for a manual edit. Hunks are never applied
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

        AttemptRun chosen = RunInOrder(original, patch);
        bool usedFuzzy = chosen.Attempts.Any(a => a.Applied && a.Tier == PatchTier.FuzzyAnchor);

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
        // Compared by key, over the whole haystack so its literals and comments are lexed in context.
        static string[] NonBlank(IEnumerable<string> keys) => [.. keys.Where(k => k.Length > 0)];

        string[] hay = NonBlank(SqlLineKeys.Compute([.. LineSplitter.Split(haystack).Select(l => l.Text)]));
        string[] pin = NonBlank(AnchorMatcher.EffectiveAnchorLines(needle));
        if (pin.Length == 0)
        {
            return true;
        }

        for (int i = 0; i + pin.Length <= hay.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pin.Length; j++)
            {
                if (!string.Equals(hay[i + j], pin[j], StringComparison.Ordinal))
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

    private static AttemptRun RunInOrder(string original, PatchDefinition patch)
    {
        string working = original;
        var attempts = new List<HunkAttempt>();
        int searchFrom = 0;

        for (int i = 0; i < patch.Hunks.Count; i++)
        {
            PatchHunk hunk = patch.Hunks[i];

            AnchorMatchResult exact = AnchorMatcher.TryApply(working, hunk.AnchorText, hunk.ReplacementText, searchFrom);
            if (exact.IsUnique && exact.PatchedText is not null)
            {
                working = exact.PatchedText;
                searchFrom = exact.EndLineExclusive;
                attempts.Add(new HunkAttempt(i, PatchTier.ExactAnchor, true, exact.FirstMatchLine, "Exact match."));
                continue;
            }

            FuzzyPatchResult fuzzy = FuzzyPatcher.TryApply(working, hunk.AnchorText, hunk.ReplacementText, searchFrom);
            if (fuzzy.Applied && fuzzy.PatchedText is not null)
            {
                working = fuzzy.PatchedText;
                searchFrom = fuzzy.EndLineExclusive;
                attempts.Add(new HunkAttempt(i, PatchTier.FuzzyAnchor, true, fuzzy.MatchLine, fuzzy.Reason));
                continue;
            }

            // Say why the exact tier could not place it when that is the more useful explanation.
            string detail = exact.Status is AnchorMatchStatus.Ambiguous or AnchorMatchStatus.OutOfOrder
                ? $"{Describe(exact)} {fuzzy.Reason}"
                : fuzzy.Reason;

            attempts.Add(new HunkAttempt(i, PatchTier.FuzzyAnchor, false, fuzzy.MatchLine, detail));
            return new AttemptRun(false, null, attempts);
        }

        return new AttemptRun(true, working, attempts);
    }

    private static string Describe(AnchorMatchResult result) => result.Status switch
    {
        AnchorMatchStatus.NotFound => "Anchor text not found.",
        AnchorMatchStatus.Ambiguous => $"The surrounding lines match {result.MatchCount} places in this object.",
        AnchorMatchStatus.OutOfOrder => $"It matched at line {result.FirstMatchLine}, above a change already placed further down.",
        _ => "No match.",
    };

    private sealed record AttemptRun(bool AllApplied, string? FinalText, List<HunkAttempt> Attempts);
}
