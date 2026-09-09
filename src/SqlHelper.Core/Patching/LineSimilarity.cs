namespace SqlHelper.Core.Patching;

/// <summary>
/// How alike two lines of code are, on a 0..1 scale. Used to decide whether a block of a client's
/// procedure is "the same lines, lightly edited" or genuinely different code.
/// </summary>
internal static class LineSimilarity
{
    /// <summary>1.0 for identical text, 0.0 for nothing in common. Compares trimmed content.</summary>
    public static double Of(string left, string right)
    {
        string a = left.Trim();
        string b = right.Trim();

        if (a.Length == 0 && b.Length == 0)
        {
            return 1.0;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0.0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1.0;
        }

        int distance = Levenshtein(a, b);
        int longest = Math.Max(a.Length, b.Length);
        return Math.Max(0.0, 1.0 - ((double)distance / longest));
    }

    /// <summary>Edit distance with two rolling rows — no full matrix, so long lines stay cheap.</summary>
    private static int Levenshtein(string a, string b)
    {
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                int deletion = previous[j] + 1;
                int insertion = current[j - 1] + 1;
                current[j] = Math.Min(substitution, Math.Min(deletion, insertion));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// Best alignment score between two blocks of lines, 0..1. Lines may be inserted or removed
    /// on either side — those simply earn nothing, and the score is normalised by the longer
    /// block, so extra lines dilute the match rather than breaking it outright.
    /// </summary>
    public static double AlignBlocks(IReadOnlyList<string> anchor, IReadOnlyList<string> window)
        => AlignBlocks(anchor.Count, window.Count, (i, j) => Of(anchor[i], window[j]));

    /// <summary>
    /// The same alignment, but taking the line-to-line score from a caller-supplied lookup.
    ///
    /// The search slides overlapping windows across the whole object, so the same pair of lines is
    /// compared over and over — and comparing two lines means an edit distance over a hundred
    /// characters or more. Letting the caller cache those pair scores turns the search from
    /// unusably slow on a long procedure into something that finishes immediately.
    /// </summary>
    public static double AlignBlocks(int anchorCount, int windowCount, Func<int, int, double> similarityOf)
    {
        ArgumentNullException.ThrowIfNull(similarityOf);

        if (anchorCount == 0 || windowCount == 0)
        {
            return 0.0;
        }

        var score = new double[anchorCount + 1, windowCount + 1];

        for (int i = 1; i <= anchorCount; i++)
        {
            for (int j = 1; j <= windowCount; j++)
            {
                double paired = score[i - 1, j - 1] + similarityOf(i - 1, j - 1);
                double skipAnchorLine = score[i - 1, j];
                double skipWindowLine = score[i, j - 1];
                score[i, j] = Math.Max(paired, Math.Max(skipAnchorLine, skipWindowLine));
            }
        }

        return score[anchorCount, windowCount] / Math.Max(anchorCount, windowCount);
    }
}

/// <summary>
/// Remembers the similarity of every (anchor line, target line) pair, so a sliding search pays for
/// each comparison once rather than once per window it appears in.
/// </summary>
internal sealed class LineSimilarityCache
{
    private readonly IReadOnlyList<string> _anchor;
    private readonly IReadOnlyList<string> _target;
    private readonly double[] _scores;

    public LineSimilarityCache(IReadOnlyList<string> anchor, IReadOnlyList<string> target)
    {
        _anchor = anchor;
        _target = target;
        _scores = new double[anchor.Count * target.Count];
        Array.Fill(_scores, -1.0);
    }

    public double Of(int anchorIndex, int targetIndex)
    {
        int slot = (anchorIndex * _target.Count) + targetIndex;
        double cached = _scores[slot];
        if (cached >= 0.0)
        {
            return cached;
        }

        double computed = LineSimilarity.Of(_anchor[anchorIndex], _target[targetIndex]);
        _scores[slot] = computed;
        return computed;
    }
}
