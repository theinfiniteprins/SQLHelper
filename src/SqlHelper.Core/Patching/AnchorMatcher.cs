namespace SqlHelper.Core.Patching;

public enum AnchorMatchStatus
{
    /// <summary>Matched at exactly one place — the only case where the tool applies anything on its own.</summary>
    Unique,

    /// <summary>The anchor text was not found — the client's code differs in this region, or it may already contain the replacement.</summary>
    NotFound,

    /// <summary>The anchor matched more than once — applying blind would risk patching the wrong occurrence.</summary>
    Ambiguous,

    /// <summary>The anchor matched once, but above a change already placed further down — the client's code is in a different order.</summary>
    OutOfOrder,
}

/// <param name="FirstMatchLine">1-based line in the target where the match starts; -1 when there is none.</param>
/// <param name="EndLineExclusive">0-based line in the patched text just past the rewritten block; -1 when nothing was applied.</param>
public sealed record AnchorMatchResult(
    AnchorMatchStatus Status,
    string? PatchedText,
    int MatchCount,
    int FirstMatchLine,
    int EndLineExclusive = -1)
{
    public bool IsUnique => Status == AnchorMatchStatus.Unique;
}

/// <summary>
/// Tier 1 of the patch pipeline: locate the anchor as a contiguous run of lines inside the target
/// and apply the change there.
///
/// Lines are compared by <see cref="SqlLineKeys"/>, so a client whose copy differs only in
/// formatting — tabs for spaces, re-aligned columns — still matches, while a difference inside a
/// string literal or identifier does not. The match must be unique in the whole object.
///
/// Applying is a merge, not a paste: within the matched block, lines the change leaves alone are
/// kept exactly as the client has them, and only the lines the change actually adds or removes are
/// rewritten. Everything outside the block is carried through byte for byte.
/// </summary>
public static class AnchorMatcher
{
    /// <param name="searchFromLine">
    /// 0-based line the match must not start above. Changes are applied in order, so each one must
    /// land below the one before it.
    /// </param>
    public static AnchorMatchResult TryApply(string target, string anchor, string replacement, int searchFromLine = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(replacement);

        List<RawLine> targetLines = LineSplitter.Split(target);
        string[] targetKeys = SqlLineKeys.Compute([.. targetLines.Select(l => l.Text)]);

        string[] anchorLines = HunkLines(anchor);
        string[] anchorKeys = SqlLineKeys.Compute(anchorLines);

        if (anchorKeys.Length == 0)
        {
            return new AnchorMatchResult(AnchorMatchStatus.NotFound, null, 0, -1);
        }

        var matchStarts = new List<int>();
        for (int i = 0; i + anchorKeys.Length <= targetKeys.Length; i++)
        {
            bool matches = true;
            for (int j = 0; j < anchorKeys.Length; j++)
            {
                if (!string.Equals(targetKeys[i + j], anchorKeys[j], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                matchStarts.Add(i);
            }
        }

        if (matchStarts.Count == 0)
        {
            return new AnchorMatchResult(AnchorMatchStatus.NotFound, null, 0, -1);
        }

        if (matchStarts.Count > 1)
        {
            return new AnchorMatchResult(AnchorMatchStatus.Ambiguous, null, matchStarts.Count, matchStarts[0] + 1);
        }

        int start = matchStarts[0];
        if (start < searchFromLine)
        {
            return new AnchorMatchResult(AnchorMatchStatus.OutOfOrder, null, 1, start + 1);
        }

        List<RawLine> window = targetLines.GetRange(start, anchorKeys.Length);
        MergeResult merged = ThreeWayLineMerge.Merge(
            anchorLines, HunkLines(replacement), window, targetKeys[start..(start + anchorKeys.Length)], LineSplitter.DominantEnding(target));

        if (!merged.Ok || merged.Merged is null)
        {
            // The block matched line for line, so a conflict is not expected here; refuse rather than guess.
            return new AnchorMatchResult(AnchorMatchStatus.NotFound, null, 0, start + 1);
        }

        string patched =
            LineSplitter.Join(targetLines.Take(start)) +
            LineSplitter.Join(merged.Merged) +
            LineSplitter.Join(targetLines.Skip(start + anchorKeys.Length));

        return new AnchorMatchResult(AnchorMatchStatus.Unique, patched, 1, start + 1, start + merged.Merged.Count);
    }

    /// <summary>
    /// The comparison keys an anchor is matched on — one per line, after dropping the empty final
    /// line the splitter produces for text ending in a newline.
    ///
    /// Whoever decides an anchor is unique must measure it exactly the way this matcher will use
    /// it; when the two disagree an anchor can pass as unique and then match twice against the
    /// client. So both go through here.
    /// </summary>
    public static string[] EffectiveAnchorLines(string anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return SqlLineKeys.Compute(HunkLines(anchor));
    }

    /// <summary>The exact lines of a hunk's text, minus the artefact empty line after a trailing newline.</summary>
    internal static string[] HunkLines(string text)
    {
        List<RawLine> lines = LineSplitter.Split(text);
        if (lines.Count > 1 && lines[^1] is { Text.Length: 0, Ending.Length: 0 })
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return [.. lines.Select(l => l.Text)];
    }

    internal static string NormalizeReplacementEnding(string replacement, string ending, bool needsTrailingEnding)
    {
        List<RawLine> lines = LineSplitter.Split(replacement);

        // A replacement authored with a trailing newline ("...\n") splits into a final empty,
        // unterminated line — an artefact, not a blank line the author meant to add. The
        // separator it represents is already carried by the previous line's Ending.
        if (lines.Count > 1 && lines[^1] is { Text.Length: 0, Ending.Length: 0 })
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var rebuilt = new List<RawLine>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            bool isLast = i == lines.Count - 1;
            string lineEnding = !isLast || needsTrailingEnding || lines[i].Ending.Length > 0 ? ending : string.Empty;
            rebuilt.Add(new RawLine(lines[i].Text, lineEnding));
        }

        return LineSplitter.Join(rebuilt);
    }
}
