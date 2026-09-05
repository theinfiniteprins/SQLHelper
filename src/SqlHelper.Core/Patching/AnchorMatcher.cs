namespace SqlHelper.Core.Patching;

public enum AnchorMatchStatus
{
    /// <summary>Matched at exactly one place — the only case where the tool applies anything on its own.</summary>
    Unique,

    /// <summary>The anchor text was not found — the client's code differs in this region, or it may already contain the replacement.</summary>
    NotFound,

    /// <summary>The anchor matched more than once — applying blind would risk patching the wrong occurrence.</summary>
    Ambiguous,
}

public sealed record AnchorMatchResult(AnchorMatchStatus Status, string? PatchedText, int MatchCount, int FirstMatchLine)
{
    public bool IsUnique => Status == AnchorMatchStatus.Unique;
}

/// <summary>
/// Tier 1 of the patch pipeline: locate the anchor as a contiguous run of lines inside the
/// target and splice in the replacement. Comparison trims each line (so indentation and
/// formatting differences don't block a match); everything outside the matched lines — and the
/// replacement text itself — is carried through verbatim, so the target's own formatting survives
/// everywhere the patch didn't touch.
/// </summary>
public static class AnchorMatcher
{
    public static AnchorMatchResult TryApply(string target, string anchor, string replacement)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(replacement);

        List<RawLine> targetLines = LineSplitter.Split(target);
        string[] anchorLines = LineSplitter.Split(anchor).Select(l => l.Trimmed).ToArray();
        // Drop a trailing blank line the splitter adds for text ending in a newline.
        if (anchorLines.Length > 1 && anchorLines[^1].Length == 0)
        {
            anchorLines = anchorLines[..^1];
        }

        if (anchorLines.Length == 0)
        {
            return new AnchorMatchResult(AnchorMatchStatus.NotFound, null, 0, -1);
        }

        var matchStarts = new List<int>();
        for (int i = 0; i + anchorLines.Length <= targetLines.Count; i++)
        {
            bool matches = true;
            for (int j = 0; j < anchorLines.Length; j++)
            {
                if (targetLines[i + j].Trimmed != anchorLines[j])
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
        int endExclusive = start + anchorLines.Length;
        string ending = LineSplitter.DominantEnding(target);

        string beforeText = LineSplitter.Join(targetLines.Take(start));
        string afterText = LineSplitter.Join(targetLines.Skip(endExclusive));
        string replacementText = NormalizeReplacementEnding(replacement, ending, needsTrailingEnding: afterText.Length > 0);

        string patched = beforeText + replacementText + afterText;
        return new AnchorMatchResult(AnchorMatchStatus.Unique, patched, 1, start + 1);
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

        // Every line but the last always separates with the target's own line-ending style.
        // The last line gets one too if something follows in the target, or if the author
        // explicitly ended their replacement with a newline; otherwise it stays bare, matching
        // an author who typed the replacement with no trailing blank line at end-of-file.
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
