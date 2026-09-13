namespace SqlHelper.Core.Patching;

/// <param name="Applied">True only when one block matched well enough, and clearly better than any rival.</param>
/// <param name="PatchedText">The spliced result; null unless <paramref name="Applied"/>.</param>
/// <param name="Confidence">How alike the matched block was to the anchor, 0..1.</param>
/// <param name="MatchLine">1-based line in the target where the matched block starts; -1 when nothing matched.</param>
/// <param name="Reason">Why it did or didn't match, in words, for the review screen.</param>
/// <param name="EndLineExclusive">0-based line in the patched text just past the rewritten block; -1 when nothing was applied.</param>
public sealed record FuzzyPatchResult(
    bool Applied,
    string? PatchedText,
    double Confidence,
    int MatchLine,
    string Reason,
    int EndLineExclusive = -1);

/// <summary>
/// Tier 2 of the patch pipeline, for a client whose code around the change has drifted — a
/// renamed variable, an inserted comment, a reformatted line — so the exact tier can't match it.
///
/// It slides a window over the target's lines and scores each candidate block by aligning it
/// against the anchor line by line. A block is only accepted when it is both similar enough in
/// absolute terms <em>and</em> clearly better than any other candidate elsewhere in the same
/// object; anything ambiguous is refused and sent to a manual edit instead. The score and the
/// line it matched at are reported so the reviewer can see exactly what was decided and why.
/// </summary>
public static class FuzzyPatcher
{
    /// <summary>A candidate block must be at least this similar to the anchor to be considered at all.</summary>
    public const double MinimumConfidence = 0.75;

    /// <summary>
    /// The winning block must beat the best rival elsewhere by this much. Two similar blocks in
    /// one procedure mean we cannot tell which one the change belongs to, so we refuse.
    /// </summary>
    public const double UniquenessMargin = 0.05;

    /// <summary>Windows this many lines longer or shorter than the anchor are also considered, to absorb inserted or deleted lines.</summary>
    private const int WindowFlex = 3;

    /// <summary>An anchor thinner than this carries too little signal to place confidently.</summary>
    public const int MinimumAnchorCharacters = 12;

    /// <param name="searchFromLine">
    /// 0-based line the chosen block must not start above, because changes are applied in order.
    /// Rival blocks are still looked for everywhere: a better-looking candidate above that line is
    /// a reason to refuse, not something to ignore.
    /// </param>
    public static FuzzyPatchResult TryApply(string target, string anchor, string replacement, int searchFromLine = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(replacement);

        if (anchor.Trim().Length < MinimumAnchorCharacters)
        {
            return new FuzzyPatchResult(false, null, 0, -1,
                "The change is too small on its own to locate safely; it needs more surrounding context.");
        }

        List<RawLine> targetLines = LineSplitter.Split(target);
        string[] anchorLines = AnchorMatcher.HunkLines(anchor);
        if (anchorLines.Length == 0 || targetLines.Count == 0)
        {
            return new FuzzyPatchResult(false, null, 0, -1, "Nothing to match against.");
        }

        // Compared by key, so formatting differences cost nothing in the score.
        string[] anchorKeys = SqlLineKeys.Compute(anchorLines);
        string[] targetKeys = SqlLineKeys.Compute([.. targetLines.Select(l => l.Text)]);

        int minLength = Math.Max(1, anchorLines.Length - WindowFlex);
        int maxLength = anchorLines.Length + WindowFlex;

        // Every window is scored against the same anchor over the same lines, so the pair scores
        // are computed once and reused; without this a long anchor makes the search take minutes.
        var similarity = new LineSimilarityCache(anchorKeys, targetKeys);
        var candidates = new List<(double Score, int Start, int Length)>();

        for (int length = minLength; length <= maxLength; length++)
        {
            for (int start = 0; start + length <= targetKeys.Length; start++)
            {
                int windowStart = start;
                double score = LineSimilarity.AlignBlocks(
                    anchorKeys.Length, length, (i, j) => similarity.Of(i, windowStart + j));
                candidates.Add((score, start, length));
            }
        }

        (double Score, int Start, int Length) best = (0, -1, 0);
        foreach (var candidate in candidates)
        {
            if (candidate.Start >= searchFromLine && candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        if (best.Start < 0 || best.Score < MinimumConfidence)
        {
            return new FuzzyPatchResult(false, null, best.Score, -1,
                $"No block in this object looked like the change (closest was {best.Score:P0} similar, " +
                $"and {MinimumConfidence:P0} is the minimum).");
        }

        (double Score, int Start, int Length) runnerUp = (0, -1, 0);
        foreach (var candidate in candidates)
        {
            if (!Overlaps(best, candidate) && candidate.Score > runnerUp.Score)
            {
                runnerUp = candidate;
            }
        }

        if (runnerUp.Start >= 0 && runnerUp.Score >= MinimumConfidence && best.Score - runnerUp.Score < UniquenessMargin)
        {
            return new FuzzyPatchResult(false, null, best.Score, best.Start + 1,
                $"Two places in this object look equally like the change (line {best.Start + 1} at {best.Score:P0} and " +
                $"line {runnerUp.Start + 1} at {runnerUp.Score:P0}), so it is not clear which one to change.");
        }

        string ending = LineSplitter.DominantEnding(target);

        // Merge rather than overwrite: anything this client has inside the matched block that the
        // change does not touch is theirs, and must survive exactly.
        List<RawLine> clientWindow = targetLines.GetRange(best.Start, best.Length);
        MergeResult merged = ThreeWayLineMerge.Merge(
            anchorLines, AnchorMatcher.HunkLines(replacement), clientWindow,
            targetKeys[best.Start..(best.Start + best.Length)], ending);

        if (!merged.Ok || merged.Merged is null)
        {
            return new FuzzyPatchResult(false, null, best.Score, best.Start + 1,
                $"Found the block at line {best.Start + 1} ({best.Score:P0} similar), but {merged.Conflict}, " +
                "so the change cannot be applied here without a decision only you can make.");
        }

        string patched =
            LineSplitter.Join(targetLines.Take(best.Start)) +
            LineSplitter.Join(merged.Merged) +
            LineSplitter.Join(targetLines.Skip(best.Start + best.Length));

        string kept = merged.KeptClientLines > 0
            ? $", keeping {merged.KeptClientLines} line(s) this client had added there"
            : string.Empty;

        return new FuzzyPatchResult(
            true,
            patched,
            best.Score,
            best.Start + 1,
            $"Matched the block at line {best.Start + 1}, {best.Score:P0} similar{kept}.",
            best.Start + merged.Merged.Count);
    }

    private static bool Overlaps((double Score, int Start, int Length) a, (double Score, int Start, int Length) b) =>
        a.Start < b.Start + b.Length && b.Start < a.Start + a.Length;
}
