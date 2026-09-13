namespace SqlHelper.Core.Patching;

/// <param name="Ok">False when the two sides changed the same lines and there is no safe answer.</param>
/// <param name="Merged">The merged block; null on conflict.</param>
/// <param name="Conflict">What clashed, in words, when <paramref name="Ok"/> is false.</param>
/// <param name="KeptClientLines">How many lines existed only on the client's side and were carried through.</param>
internal sealed record MergeResult(bool Ok, IReadOnlyList<RawLine>? Merged, string? Conflict, int KeptClientLines);

/// <summary>
/// Merges a change into a block of a client's code the way version control does (diff3), with
/// the authored "before" text as the common ancestor:
///
///   base   = the anchor: the reference procedure as it was
///   ours   = the replacement: the reference procedure with the fix
///   theirs = the block found in this client's procedure
///
/// The algorithm, exactly as in diff3:
///
///   1. Diff base→ours (exact text: every character of the authored change counts) and
///      base→theirs (by <see cref="SqlLineKeys"/>: the client's formatting is not a change).
///   2. A base line unchanged on <em>both</em> sides is a synchronisation point. Between two
///      synchronisation points lies a chunk of base, ours and theirs.
///   3. Each chunk resolves by one rule:
///        ours unchanged                 → keep theirs  (the client's own text, untouched)
///        theirs unchanged               → take ours    (the change, where the client agrees)
///        ours and theirs made the same  → keep theirs
///        anything else                  → conflict: refuse, and leave the client alone
///   4. At every synchronisation point the client's own line is kept, formatting and all.
///
/// There is no running offset or index arithmetic to drift out of step: each chunk is resolved
/// from the three aligned slices in front of it and nothing else.
/// </summary>
internal static class ThreeWayLineMerge
{
    public static MergeResult Merge(
        IReadOnlyList<string> baseLines,
        IReadOnlyList<string> replacementLines,
        IReadOnlyList<RawLine> clientLines,
        string lineEnding)
        => Merge(baseLines, replacementLines, clientLines, SqlLineKeys.Compute([.. clientLines.Select(l => l.Text)]), lineEnding);

    /// <param name="clientKeys">
    /// Keys for <paramref name="clientLines"/>, computed over the client's whole object so a block
    /// that begins inside a multi-line literal is keyed correctly.
    /// </param>
    public static MergeResult Merge(
        IReadOnlyList<string> baseLines,
        IReadOnlyList<string> replacementLines,
        IReadOnlyList<RawLine> clientLines,
        IReadOnlyList<string> clientKeys,
        string lineEnding)
    {
        ArgumentNullException.ThrowIfNull(baseLines);
        ArgumentNullException.ThrowIfNull(replacementLines);
        ArgumentNullException.ThrowIfNull(clientLines);
        ArgumentNullException.ThrowIfNull(clientKeys);

        string[] baseKeys = SqlLineKeys.Compute(baseLines);
        string[] oursKeys = SqlLineKeys.Compute(replacementLines);

        // Where a block contains repeated lines, a diff has to break ties, and the tie-break could
        // decide which copy the change lands next to. So the merge is done with the earliest and
        // the latest possible alignment on each side. If every combination produces the same code,
        // the answer did not depend on the tie-break. If they disagree, where the change belongs
        // genuinely cannot be known from the text - so it is refused, not guessed.
        int[][] oursMaps =
        [
            MapEqualLines(LineDiff.Compute(baseLines, replacementLines), baseLines.Count),
            MapEqualLines(LineDiff.ComputeLateBiased(baseLines, replacementLines), baseLines.Count),
        ];
        int[][] theirsMaps =
        [
            MapEqualLines(LineDiff.Compute(baseKeys, clientKeys), baseLines.Count),
            MapEqualLines(LineDiff.ComputeLateBiased(baseKeys, clientKeys), baseLines.Count),
        ];

        MergeResult? first = null;
        string[]? firstKeys = null;

        foreach (int[] oursMap in oursMaps)
        {
            foreach (int[] theirsMap in theirsMaps)
            {
                MergeResult result = MergeWith(baseLines, replacementLines, clientLines, baseKeys, oursKeys, clientKeys, oursMap, theirsMap, lineEnding);
                if (!result.Ok)
                {
                    return result;
                }

                string[] keys = SqlLineKeys.Compute([.. result.Merged!.Select(l => l.Text)]);
                if (first is null)
                {
                    first = result;
                    firstKeys = keys;
                }
                else if (!keys.SequenceEqual(firstKeys!, StringComparer.Ordinal))
                {
                    return new MergeResult(false, null,
                        "the lines around the change repeat in this client's copy, so where the change belongs is ambiguous",
                        0);
                }
            }
        }

        return first!;
    }

    private static MergeResult MergeWith(
        IReadOnlyList<string> baseLines,
        IReadOnlyList<string> replacementLines,
        IReadOnlyList<RawLine> clientLines,
        string[] baseKeys,
        string[] oursKeys,
        IReadOnlyList<string> clientKeys,
        int[] baseToOurs,
        int[] baseToTheirs,
        string lineEnding)
    {
        var output = new List<RawLine>();
        int keptClientLines = 0;

        int b = 0;
        int o = 0;
        int t = 0;

        while (true)
        {
            // Next base line that both sides left untouched, or the end of everything.
            int sync = b;
            while (sync < baseLines.Count && !(baseToOurs[sync] >= o && baseToTheirs[sync] >= t))
            {
                sync++;
            }

            int oEnd = sync < baseLines.Count ? baseToOurs[sync] : replacementLines.Count;
            int tEnd = sync < baseLines.Count ? baseToTheirs[sync] : clientLines.Count;

            bool oursUnchanged = RangeEquals(baseLines, b, sync, replacementLines, o, oEnd);
            bool theirsUnchanged = RangeEquals(baseKeys, b, sync, clientKeys, t, tEnd);

            if (oursUnchanged)
            {
                AppendClient(output, clientLines, t, tEnd);
                if (!theirsUnchanged)
                {
                    keptClientLines += tEnd - t;
                }
            }
            else if (theirsUnchanged)
            {
                for (int i = o; i < oEnd; i++)
                {
                    output.Add(new RawLine(replacementLines[i], lineEnding));
                }
            }
            else if (RangeEquals(oursKeys, o, oEnd, clientKeys, t, tEnd))
            {
                AppendClient(output, clientLines, t, tEnd); // both made the same change
            }
            else if (TryInterleave(baseLines, b, sync, replacementLines, o, oEnd, clientLines, baseKeys, clientKeys, t, tEnd, lineEnding, output, out int added))
            {
                keptClientLines += added;
            }
            else
            {
                return new MergeResult(false, null,
                    "this client has its own version of the line(s) the change rewrites " +
                    $"(reference has \"{Describe(baseLines, b, sync)}\", this client has \"{DescribeClient(clientLines, t, tEnd)}\")",
                    keptClientLines);
            }

            if (sync >= baseLines.Count)
            {
                break;
            }

            output.Add(clientLines[baseToTheirs[sync]]); // unchanged on both sides: the client's own line
            b = sync + 1;
            o = baseToOurs[sync] + 1;
            t = baseToTheirs[sync] + 1;
        }

        FixEndings(output, clientLines, lineEnding);
        return new MergeResult(true, output, null, keptClientLines);
    }

    /// <summary>
    /// Resolves a chunk that plain diff3 would call a conflict, in the one situation where the
    /// answer is still certain: the client only <em>added</em> lines, and none of them sits inside
    /// the span of lines the change rewrites.
    ///
    /// Picture a client that wrote a comment directly above the line being changed. The change and
    /// the comment touch neighbouring lines, so diff3 gives up — but every base line is still there
    /// on the client's side, and the comment's position relative to them is not in doubt. So the
    /// base lines are walked one gap at a time: the client's additions go in at their gap, the
    /// change is applied to the lines it rewrites, and the client's own text is kept for every line
    /// the change leaves alone.
    ///
    /// Anything less clear-cut stays a conflict:
    ///   • the client edited or removed any base line in the chunk;
    ///   • the client added lines strictly inside a span the change rewrites (those lines are gone);
    ///   • the client and the change both added lines at the same gap (their order is unknowable).
    /// </summary>
    private static bool TryInterleave(
        IReadOnlyList<string> baseLines, int bStart, int bEnd,
        IReadOnlyList<string> oursLines, int oStart, int oEnd,
        IReadOnlyList<RawLine> clientLines,
        string[] baseKeys, IReadOnlyList<string> clientKeys, int tStart, int tEnd,
        string lineEnding,
        List<RawLine> output,
        out int addedClientLines)
    {
        addedClientLines = 0;
        int n = bEnd - bStart;

        // The client's side of the chunk: every base line must survive unchanged, in order.
        string[] baseChunkKeys = [.. Enumerable.Range(bStart, n).Select(i => baseKeys[i])];
        string[] clientChunkKeys = [.. Enumerable.Range(tStart, tEnd - tStart).Select(i => clientKeys[i])];
        IReadOnlyList<LineDiffOp> theirs = LineDiff.Compute(baseChunkKeys, clientChunkKeys);

        if (theirs.Any(op => op.Kind == LineDiffKind.Delete))
        {
            return false;
        }

        var clientIndexOfBase = new int[n];
        var clientInsertsAtGap = new List<int>[n + 1];
        foreach (LineDiffOp op in theirs)
        {
            if (op.Kind == LineDiffKind.Equal)
            {
                for (int k = 0; k < op.Count; k++)
                {
                    clientIndexOfBase[op.OldStart + k] = tStart + op.NewStart + k;
                }
            }
            else
            {
                (clientInsertsAtGap[op.OldStart] ??= []).AddRange(Enumerable.Range(tStart + op.NewStart, op.Count));
            }
        }

        // The change's side: which base lines it rewrites, and what it puts in their place.
        string[] baseChunk = [.. Enumerable.Range(bStart, n).Select(i => baseLines[i])];
        string[] oursChunk = [.. Enumerable.Range(oStart, oEnd - oStart).Select(i => oursLines[i])];
        var regions = new List<(int From, int To, List<string> Lines)>();

        IReadOnlyList<LineDiffOp> ours = LineDiff.Compute(baseChunk, oursChunk);
        for (int i = 0; i < ours.Count; i++)
        {
            if (ours[i].Kind == LineDiffKind.Equal)
            {
                continue;
            }

            int from = ours[i].OldStart;
            int to = from;
            var lines = new List<string>();
            while (i < ours.Count && ours[i].Kind != LineDiffKind.Equal)
            {
                if (ours[i].Kind == LineDiffKind.Delete)
                {
                    to += ours[i].Count;
                }
                else
                {
                    lines.AddRange(Enumerable.Range(ours[i].NewStart, ours[i].Count).Select(k => oursChunk[k]));
                }

                i++;
            }

            i--;
            regions.Add((from, to, lines));
        }

        for (int gap = 0; gap <= n; gap++)
        {
            if (clientInsertsAtGap[gap] is null)
            {
                continue;
            }

            foreach ((int from, int to, List<string> _) in regions)
            {
                bool strictlyInside = from < gap && gap < to;
                bool sameGapInsertion = from == to && from == gap;
                if (strictlyInside || sameGapInsertion)
                {
                    return false;
                }
            }
        }

        var result = new List<RawLine>();
        int regionIndex = 0;
        int p = 0;
        while (p <= n)
        {
            if (clientInsertsAtGap[p] is { } inserts)
            {
                foreach (int index in inserts)
                {
                    result.Add(clientLines[index]);
                    addedClientLines++;
                }
            }

            if (regionIndex < regions.Count && regions[regionIndex].From == p)
            {
                foreach (string line in regions[regionIndex].Lines)
                {
                    result.Add(new RawLine(line, lineEnding));
                }

                int to = regions[regionIndex].To;
                regionIndex++;
                if (to > p)
                {
                    p = to;
                    continue;
                }
            }

            if (p < n)
            {
                result.Add(clientLines[clientIndexOfBase[p]]);
            }

            p++;
        }

        output.AddRange(result);
        return true;
    }

    /// <summary>For each base line, the index it is equal to on the other side, or -1 if changed.</summary>
    private static int[] MapEqualLines(IReadOnlyList<LineDiffOp> ops, int baseCount)
    {
        var map = new int[baseCount];
        Array.Fill(map, -1);

        foreach (LineDiffOp op in ops)
        {
            if (op.Kind != LineDiffKind.Equal)
            {
                continue;
            }

            for (int k = 0; k < op.Count; k++)
            {
                map[op.OldStart + k] = op.NewStart + k;
            }
        }

        return map;
    }

    private static bool RangeEquals(IReadOnlyList<string> left, int lStart, int lEnd, IReadOnlyList<string> right, int rStart, int rEnd)
    {
        if (lEnd - lStart != rEnd - rStart)
        {
            return false;
        }

        for (int k = 0; k < lEnd - lStart; k++)
        {
            if (!string.Equals(left[lStart + k], right[rStart + k], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendClient(List<RawLine> output, IReadOnlyList<RawLine> clientLines, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            output.Add(clientLines[i]);
        }
    }

    /// <summary>
    /// Every line but the last is terminated; the last keeps whatever terminated the client's block,
    /// so the merged block slots back into the object exactly where the original one sat.
    /// </summary>
    private static void FixEndings(List<RawLine> output, IReadOnlyList<RawLine> clientLines, string lineEnding)
    {
        string finalEnding = clientLines.Count > 0 ? clientLines[^1].Ending : lineEnding;

        for (int i = 0; i < output.Count; i++)
        {
            bool isLast = i == output.Count - 1;
            string wanted = isLast ? finalEnding : (output[i].Ending.Length > 0 ? output[i].Ending : lineEnding);
            if (!string.Equals(output[i].Ending, wanted, StringComparison.Ordinal))
            {
                output[i] = output[i] with { Ending = wanted };
            }
        }
    }

    /// <summary>A short, readable quote of a range of lines, for a conflict message.</summary>
    private static string Describe(IReadOnlyList<string> lines, int start, int end)
    {
        string text = string.Join(" / ", Enumerable.Range(start, Math.Max(0, end - start)).Select(i => lines[i].Trim()).Where(p => p.Length > 0));
        return text.Length == 0 ? "(nothing)" : text.Length <= 90 ? text : text[..90] + "…";
    }

    private static string DescribeClient(IReadOnlyList<RawLine> lines, int start, int end) =>
        Describe([.. lines.Select(l => l.Text)], start, end);
}
