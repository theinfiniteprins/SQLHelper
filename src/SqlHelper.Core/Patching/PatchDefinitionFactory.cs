using DiffPlex;
using DiffPlex.Model;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Patching;

/// <summary>Outcome of building a <see cref="PatchDefinition"/> from a before/after reference pair.</summary>
public sealed record PatchDefinitionResult(bool Ok, PatchDefinition? Patch, string? Error)
{
    public static PatchDefinitionResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Builds a patch by diffing two full versions of the reference object — the natural way a
/// DBA already has the change in hand ("here's the old canonical procedure, here's the fixed
/// one"). Any <c>USE</c> / <c>GO</c> / <c>SET</c> preamble the operator pasted in (SSMS's
/// "Script as ALTER" header, most commonly) is stripped from both sides before diffing, so a
/// difference there is never mistaken for part of the change.
///
/// Each change carries enough surrounding context to identify <em>where</em> it goes. A fixed
/// couple of lines is not enough for real T-SQL: a large procedure repeats whole blocks verbatim
/// (the same <c>@StatusType</c> filter under two different joins, say), so a short anchor matches
/// in several places, the tool cannot tell which one is meant, and — quite correctly — refuses.
/// Refusing is safe but useless if it happens on a fifth of all edits.
///
/// So context is not fixed: every anchor grows until it appears exactly once in the reference
/// version. Growth is minimal — just enough to be unambiguous — because a longer anchor also
/// demands more of a client that has drifted. Anchors that grow into each other are merged into
/// a single hunk, since two hunks sharing lines cannot both be applied.
/// </summary>
public static class PatchDefinitionFactory
{
    private const int DefaultContextLines = 2;

    /// <summary>Anchors stop growing here; by this point the anchor spans the whole reference anyway.</summary>
    private const int MaxGrowthRounds = 20;

    /// <summary>
    /// How much context an anchor may grow to before the attempt is abandoned.
    ///
    /// Growing is how a repeated block is told apart from its twin, but it is not free: a longer
    /// anchor demands more of a client that has drifted, and the tolerant tier's search cost rises
    /// with it. If this much context still cannot pin the change down, the honest answer is that
    /// the object is too self-similar to place it automatically — which the operator is told.
    /// </summary>
    public const int MaxContextLines = 120;

    public static PatchDefinitionResult FromReferenceDiff(
        string changeTitle,
        string beforeScript,
        string afterScript,
        int contextLines = DefaultContextLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(beforeScript);
        ArgumentException.ThrowIfNullOrWhiteSpace(afterScript);

        ModuleExtraction before = TSqlNormalizer.ExtractModule(beforeScript);
        if (!before.Ok)
        {
            return PatchDefinitionResult.Failure($"'Before' script: {before.Error}");
        }

        ModuleExtraction after = TSqlNormalizer.ExtractModule(afterScript);
        if (!after.Ok)
        {
            return PatchDefinitionResult.Failure($"'After' script: {after.Error}");
        }

        if (!Equals(before.Name, after.Name))
        {
            return PatchDefinitionResult.Failure(
                $"'Before' script is for {before.Name} but 'after' is for {after.Name} — they must be the same object.");
        }

        var differ = new Differ();
        DiffResult diff = differ.CreateLineDiffs(before.ModuleText, after.ModuleText, ignoreWhitespace: false);

        if (diff.DiffBlocks.Count == 0)
        {
            return PatchDefinitionResult.Failure("The two versions are identical — there is nothing to patch.");
        }

        List<DiffBlock> blocks = [.. diff.DiffBlocks];
        string[] oldLines = [.. diff.PiecesOld];
        string[] newLines = [.. diff.PiecesNew];

        // Comparison is by trimmed line, exactly as AnchorMatcher will compare against the client,
        // so an anchor judged unique here is unique by the same rule that will be applied there.
        string[] oldTrimmed = [.. oldLines.Select(l => l.Trim())];

        List<Region> regions = [.. Enumerable.Range(0, blocks.Count).Select(i => new Region(i, i, Math.Max(0, contextLines)))];

        for (int round = 0; round <= MaxGrowthRounds; round++)
        {
            regions = Merge(regions, blocks, oldLines.Length);

            bool grew = false;
            foreach (Region region in regions)
            {
                (int start, int end) = region.AnchorRange(blocks, oldLines.Length);

                // Measured on the anchor exactly as AnchorMatcher will use it, not on the raw
                // line range - the two must agree or "unique here" means nothing over there.
                string[] effective = AnchorMatcher.EffectiveAnchorLines(string.Join('\n', oldLines[start..end]));
                if (CountOccurrences(oldTrimmed, effective) <= 1)
                {
                    continue;
                }

                if (start == 0 && end == oldLines.Length)
                {
                    continue; // already the whole reference; nothing more to give
                }

                if (region.Context >= MaxContextLines)
                {
                    continue; // too self-similar to pin down; the matcher will say so plainly
                }

                region.Context = Math.Min(MaxContextLines, region.Context <= 0 ? 1 : region.Context * 2);
                grew = true;
            }

            if (!grew)
            {
                break;
            }
        }

        var hunks = new List<PatchHunk>(regions.Count);
        foreach (Region region in regions)
        {
            (int anchorStart, int anchorEnd) = region.AnchorRange(blocks, oldLines.Length);
            (int replacementStart, int replacementEnd) = region.ReplacementRange(blocks, anchorStart, anchorEnd, newLines.Length);

            string anchor = string.Join('\n', oldLines[anchorStart..anchorEnd]);
            string replacement = string.Join('\n', newLines[replacementStart..replacementEnd]);

            hunks.Add(new PatchHunk(anchor, replacement, blocks[region.FirstBlock].DeleteStartA + 1));
        }

        var patch = new PatchDefinition(changeTitle, before.Name!, hunks);
        return new PatchDefinitionResult(true, patch, null);
    }

    /// <summary>
    /// Folds together regions whose anchors touch or overlap.
    ///
    /// Hunks are applied one after another to the same text, so if hunk 2's anchor includes lines
    /// hunk 1 has already rewritten, hunk 2 can no longer find itself and the whole patch is
    /// abandoned. Two changes close enough to share context are really one change.
    /// </summary>
    private static List<Region> Merge(List<Region> regions, IReadOnlyList<DiffBlock> blocks, int oldLineCount)
    {
        var merged = new List<Region>(regions.Count);

        foreach (Region region in regions.OrderBy(r => r.FirstBlock))
        {
            if (merged.Count == 0)
            {
                merged.Add(region);
                continue;
            }

            Region previous = merged[^1];
            (_, int previousEnd) = previous.AnchorRange(blocks, oldLineCount);
            (int start, _) = region.AnchorRange(blocks, oldLineCount);

            if (previousEnd >= start)
            {
                previous.LastBlock = Math.Max(previous.LastBlock, region.LastBlock);
                previous.Context = Math.Max(previous.Context, region.Context);
            }
            else
            {
                merged.Add(region);
            }
        }

        return merged;
    }

    /// <summary>How many times the anchor occurs in the reference, compared by trimmed line.</summary>
    private static int CountOccurrences(string[] trimmed, string[] anchorLines)
    {
        if (anchorLines.Length == 0)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i + anchorLines.Length <= trimmed.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < anchorLines.Length; j++)
            {
                if (!string.Equals(trimmed[i + j], anchorLines[j], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }

            if (match && ++count > 1)
            {
                return count; // two is all we need to know
            }
        }

        return count;
    }

    /// <summary>One or more adjacent diff blocks, plus however much surrounding context they currently need.</summary>
    private sealed class Region(int firstBlock, int lastBlock, int context)
    {
        public int FirstBlock { get; } = firstBlock;

        public int LastBlock { get; set; } = lastBlock;

        public int Context { get; set; } = context;

        public (int Start, int End) AnchorRange(IReadOnlyList<DiffBlock> blocks, int oldLineCount)
        {
            DiffBlock first = blocks[FirstBlock];
            DiffBlock last = blocks[LastBlock];

            int start = Math.Max(0, first.DeleteStartA - Context);
            int end = Math.Min(oldLineCount, last.DeleteStartA + last.DeleteCountA + Context);
            return (start, Math.Max(start, end));
        }

        /// <summary>
        /// The same window in the "after" version. The context lines on either side are unchanged
        /// text, so they shift by a fixed amount — whatever the diff inserted or removed before
        /// them at this point.
        /// </summary>
        public (int Start, int End) ReplacementRange(IReadOnlyList<DiffBlock> blocks, int anchorStart, int anchorEnd, int newLineCount)
        {
            DiffBlock first = blocks[FirstBlock];
            DiffBlock last = blocks[LastBlock];

            int shiftBefore = first.InsertStartB - first.DeleteStartA;
            int shiftAfter = (last.InsertStartB + last.InsertCountB) - (last.DeleteStartA + last.DeleteCountA);

            int start = Math.Clamp(anchorStart + shiftBefore, 0, newLineCount);
            int end = Math.Clamp(anchorEnd + shiftAfter, start, newLineCount);
            return (start, end);
        }
    }
}
