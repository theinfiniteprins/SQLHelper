using DiffPlex;
using DiffPlex.Model;

namespace SqlHelper.Core.Patching;

public enum LineDiffKind
{
    Equal,
    Delete,
    Insert,
}

/// <summary>A run of lines: equal in both, removed from the old side, or added on the new side.</summary>
/// <param name="OldStart">Index into the old lines (for Equal and Delete; the position for Insert).</param>
/// <param name="NewStart">Index into the new lines (for Equal and Insert; the position for Delete).</param>
public readonly record struct LineDiffOp(LineDiffKind Kind, int OldStart, int NewStart, int Count);

/// <summary>
/// Line-level diff that aligns code the way a person reads it.
///
/// A plain shortest-edit diff (what DiffPlex computes) is free to pair up any identical lines, and
/// in T-SQL most lines are not distinctive — <c>(</c>, <c>)</c>, <c>AND</c>, <c>END</c>, blank lines.
/// Change three or four lines and it will happily match your new lines against a stray <c>)</c>
/// twenty lines away, splitting one change into several scattered fragments. That is confusing to
/// review, and each fragment then has to be located on every client separately.
///
/// This anchors on lines that occur exactly once on both sides first (the "patience" approach),
/// which pins the alignment to the distinctive lines, and only then fills the gaps between them
/// with an exact longest-common-subsequence. Every result is checked — the operations must rebuild
/// the new text from the old, line for line — and if that check ever failed the DiffPlex result
/// would be used instead. A diff is only allowed to be different in <em>shape</em>, never wrong.
/// </summary>
public static class LineDiff
{
    /// <summary>Above this many cells, the gap-filling LCS hands over to DiffPlex's linear-space algorithm.</summary>
    private const long MaxLcsCells = 4_000_000;

    public static IReadOnlyList<LineDiffOp> Compute(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        ArgumentNullException.ThrowIfNull(oldLines);
        ArgumentNullException.ThrowIfNull(newLines);

        var raw = new List<LineDiffOp>();
        try
        {
            Recurse(oldLines, 0, oldLines.Count, newLines, 0, newLines.Count, raw);
            List<LineDiffOp> ops = Normalise(raw);
            if (IsValid(oldLines, newLines, ops))
            {
                return ops;
            }
        }
        catch (InsufficientExecutionStackException)
        {
            // Pathologically deep recursion: fall through to the proven implementation.
        }

        List<LineDiffOp> fallback = Normalise(FromDiffPlex(oldLines, newLines));
        if (!IsValid(oldLines, newLines, fallback))
        {
            throw new InvalidOperationException("Could not produce a verifiable diff for these lines.");
        }

        return fallback;
    }

    /// <summary>
    /// The same diff, but where repeated lines leave a choice, matching them as late as possible
    /// instead of as early as possible. Comparing a result built from each tells a caller whether
    /// the answer depended on an arbitrary tie-break.
    /// </summary>
    public static IReadOnlyList<LineDiffOp> ComputeLateBiased(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        ArgumentNullException.ThrowIfNull(oldLines);
        ArgumentNullException.ThrowIfNull(newLines);

        string[] oldReversed = [.. oldLines.Reverse()];
        string[] newReversed = [.. newLines.Reverse()];

        // Diff the reversed text, then walk its operations backwards; positions are rebuilt.
        List<LineDiffOp> ops = Normalise([.. Compute(oldReversed, newReversed).Reverse()]);
        return IsValid(oldLines, newLines, ops) ? ops : Compute(oldLines, newLines);
    }

    /// <summary>
    /// True when <paramref name="ops"/> rebuilds <paramref name="newLines"/> from
    /// <paramref name="oldLines"/> exactly: every line accounted for once, in order, and every
    /// "equal" line genuinely equal.
    /// </summary>
    public static bool IsValid(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, IReadOnlyList<LineDiffOp> ops)
    {
        int o = 0;
        int n = 0;

        foreach (LineDiffOp op in ops)
        {
            if (op.Count <= 0 || op.OldStart != o || op.NewStart != n)
            {
                return false;
            }

            switch (op.Kind)
            {
                case LineDiffKind.Equal:
                    for (int k = 0; k < op.Count; k++)
                    {
                        if (o + k >= oldLines.Count || n + k >= newLines.Count
                            || !string.Equals(oldLines[o + k], newLines[n + k], StringComparison.Ordinal))
                        {
                            return false;
                        }
                    }

                    o += op.Count;
                    n += op.Count;
                    break;

                case LineDiffKind.Delete:
                    o += op.Count;
                    break;

                case LineDiffKind.Insert:
                    n += op.Count;
                    break;
            }
        }

        return o == oldLines.Count && n == newLines.Count;
    }

    // ------------------------------------------------------------------ patience anchoring

    private static void Recurse(
        IReadOnlyList<string> a, int aLo, int aHi,
        IReadOnlyList<string> b, int bLo, int bHi,
        List<LineDiffOp> output)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();

        // Common prefix and suffix are trivially equal.
        int prefix = 0;
        while (aLo + prefix < aHi && bLo + prefix < bHi
               && string.Equals(a[aLo + prefix], b[bLo + prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        if (prefix > 0)
        {
            output.Add(new LineDiffOp(LineDiffKind.Equal, aLo, bLo, prefix));
            aLo += prefix;
            bLo += prefix;
        }

        int suffix = 0;
        while (aHi - suffix > aLo && bHi - suffix > bLo
               && string.Equals(a[aHi - 1 - suffix], b[bHi - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        int aEnd = aHi - suffix;
        int bEnd = bHi - suffix;

        if (aLo == aEnd || bLo == bEnd)
        {
            if (aLo < aEnd)
            {
                output.Add(new LineDiffOp(LineDiffKind.Delete, aLo, bLo, aEnd - aLo));
            }

            if (bLo < bEnd)
            {
                output.Add(new LineDiffOp(LineDiffKind.Insert, aEnd, bLo, bEnd - bLo));
            }
        }
        else
        {
            List<(int A, int B)> anchors = UniqueCommonAnchors(a, aLo, aEnd, b, bLo, bEnd);

            if (anchors.Count == 0)
            {
                FillGap(a, aLo, aEnd, b, bLo, bEnd, output);
            }
            else
            {
                int ai = aLo;
                int bi = bLo;
                foreach ((int anchorA, int anchorB) in anchors)
                {
                    Recurse(a, ai, anchorA, b, bi, anchorB, output);
                    output.Add(new LineDiffOp(LineDiffKind.Equal, anchorA, anchorB, 1));
                    ai = anchorA + 1;
                    bi = anchorB + 1;
                }

                Recurse(a, ai, aEnd, b, bi, bEnd, output);
            }
        }

        if (suffix > 0)
        {
            output.Add(new LineDiffOp(LineDiffKind.Equal, aEnd, bEnd, suffix));
        }
    }

    /// <summary>
    /// Lines occurring exactly once in each range, paired up, reduced to the longest run that keeps
    /// the same order on both sides.
    /// </summary>
    private static List<(int A, int B)> UniqueCommonAnchors(
        IReadOnlyList<string> a, int aLo, int aHi, IReadOnlyList<string> b, int bLo, int bHi)
    {
        var counts = new Dictionary<string, (int CountA, int IndexA, int CountB, int IndexB)>(StringComparer.Ordinal);

        for (int i = aLo; i < aHi; i++)
        {
            counts.TryGetValue(a[i], out var c);
            counts[a[i]] = (c.CountA + 1, i, c.CountB, c.IndexB);
        }

        for (int j = bLo; j < bHi; j++)
        {
            if (counts.TryGetValue(b[j], out var c))
            {
                counts[b[j]] = (c.CountA, c.IndexA, c.CountB + 1, j);
            }
        }

        var pairs = counts.Values
            .Where(c => c.CountA == 1 && c.CountB == 1)
            .Select(c => (A: c.IndexA, B: c.IndexB))
            .OrderBy(p => p.A)
            .ToList();

        return LongestIncreasingByB(pairs);
    }

    /// <summary>Patience sorting: the longest subsequence whose B positions increase.</summary>
    private static List<(int A, int B)> LongestIncreasingByB(List<(int A, int B)> pairs)
    {
        if (pairs.Count == 0)
        {
            return pairs;
        }

        var tails = new List<int>();          // index into pairs of the smallest tail for each length
        var previous = new int[pairs.Count];

        for (int i = 0; i < pairs.Count; i++)
        {
            int lo = 0;
            int hi = tails.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (pairs[tails[mid]].B < pairs[i].B)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            previous[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count)
            {
                tails.Add(i);
            }
            else
            {
                tails[lo] = i;
            }
        }

        var result = new List<(int A, int B)>(tails.Count);
        for (int k = tails[^1]; k >= 0; k = previous[k])
        {
            result.Add(pairs[k]);
        }

        result.Reverse();
        return result;
    }

    // ------------------------------------------------------------------ gap filling

    /// <summary>A range with no unique lines to anchor on: exact LCS, or DiffPlex when too large for a table.</summary>
    private static void FillGap(
        IReadOnlyList<string> a, int aLo, int aHi, IReadOnlyList<string> b, int bLo, int bHi, List<LineDiffOp> output)
    {
        int m = aHi - aLo;
        int n = bHi - bLo;

        if ((long)m * n > MaxLcsCells)
        {
            foreach (LineDiffOp op in FromDiffPlex(Slice(a, aLo, aHi), Slice(b, bLo, bHi)))
            {
                output.Add(op with { OldStart = op.OldStart + aLo, NewStart = op.NewStart + bLo });
            }

            return;
        }

        // lcs[i, j] = LCS length of a[aLo + i ..] and b[bLo + j ..]
        var lcs = new int[m + 1, n + 1];
        for (int i = m - 1; i >= 0; i--)
        {
            for (int j = n - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(a[aLo + i], b[bLo + j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0;
        int y = 0;
        while (x < m && y < n)
        {
            if (string.Equals(a[aLo + x], b[bLo + y], StringComparison.Ordinal) && lcs[x, y] == lcs[x + 1, y + 1] + 1)
            {
                output.Add(new LineDiffOp(LineDiffKind.Equal, aLo + x, bLo + y, 1));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                output.Add(new LineDiffOp(LineDiffKind.Delete, aLo + x, bLo + y, 1));
                x++;
            }
            else
            {
                output.Add(new LineDiffOp(LineDiffKind.Insert, aLo + x, bLo + y, 1));
                y++;
            }
        }

        if (x < m)
        {
            output.Add(new LineDiffOp(LineDiffKind.Delete, aLo + x, bLo + y, m - x));
        }

        if (y < n)
        {
            output.Add(new LineDiffOp(LineDiffKind.Insert, aLo + m, bLo + y, n - y));
        }
    }

    private static string[] Slice(IReadOnlyList<string> lines, int lo, int hi)
    {
        var slice = new string[hi - lo];
        for (int i = lo; i < hi; i++)
        {
            slice[i - lo] = lines[i];
        }

        return slice;
    }

    private static List<LineDiffOp> FromDiffPlex(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // Lines are passed as whole chunks so DiffPlex never re-splits or trims them. Each carries a
        // marker so that "no lines" and "one empty line" cannot be confused.
        static string Pack(IReadOnlyList<string> lines) => string.Concat(lines.Select(l => "" + l + ""));
        static string[] Unpack(string text) => text.Length == 0 ? [] : text[..^1].Split('');

        var differ = new Differ();
        DiffResult result = differ.CreateCustomDiffs(Pack(a), Pack(b), ignoreWhiteSpace: false, Unpack);

        var ops = new List<LineDiffOp>();
        int o = 0;
        int n = 0;
        foreach (DiffBlock block in result.DiffBlocks.OrderBy(x => x.DeleteStartA))
        {
            int equal = block.DeleteStartA - o;
            if (equal > 0)
            {
                ops.Add(new LineDiffOp(LineDiffKind.Equal, o, n, equal));
                o += equal;
                n += equal;
            }

            if (block.DeleteCountA > 0)
            {
                ops.Add(new LineDiffOp(LineDiffKind.Delete, o, n, block.DeleteCountA));
                o += block.DeleteCountA;
            }

            if (block.InsertCountB > 0)
            {
                ops.Add(new LineDiffOp(LineDiffKind.Insert, o, n, block.InsertCountB));
                n += block.InsertCountB;
            }
        }

        if (o < a.Count)
        {
            ops.Add(new LineDiffOp(LineDiffKind.Equal, o, n, a.Count - o));
        }

        return ops;
    }

    /// <summary>
    /// Merges adjacent runs of the same kind, and within each changed region puts every removal
    /// before every addition — the order a reader expects, and one canonical form for everything
    /// downstream. Positions are recomputed from scratch, so the result is always contiguous.
    /// </summary>
    private static List<LineDiffOp> Normalise(List<LineDiffOp> raw)
    {
        var result = new List<LineDiffOp>();
        int o = 0;
        int n = 0;
        int i = 0;

        while (i < raw.Count)
        {
            if (raw[i].Kind == LineDiffKind.Equal)
            {
                int count = 0;
                while (i < raw.Count && raw[i].Kind == LineDiffKind.Equal)
                {
                    count += raw[i].Count;
                    i++;
                }

                if (count > 0)
                {
                    result.Add(new LineDiffOp(LineDiffKind.Equal, o, n, count));
                    o += count;
                    n += count;
                }

                continue;
            }

            int deleted = 0;
            int inserted = 0;
            while (i < raw.Count && raw[i].Kind != LineDiffKind.Equal)
            {
                if (raw[i].Kind == LineDiffKind.Delete)
                {
                    deleted += raw[i].Count;
                }
                else
                {
                    inserted += raw[i].Count;
                }

                i++;
            }

            if (deleted > 0)
            {
                result.Add(new LineDiffOp(LineDiffKind.Delete, o, n, deleted));
                o += deleted;
            }

            if (inserted > 0)
            {
                result.Add(new LineDiffOp(LineDiffKind.Insert, o, n, inserted));
                n += inserted;
            }
        }

        return result;
    }
}
