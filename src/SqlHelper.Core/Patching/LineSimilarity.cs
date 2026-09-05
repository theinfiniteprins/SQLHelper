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
    {
        if (anchor.Count == 0 || window.Count == 0)
        {
            return 0.0;
        }

        var score = new double[anchor.Count + 1, window.Count + 1];

        for (int i = 1; i <= anchor.Count; i++)
        {
            for (int j = 1; j <= window.Count; j++)
            {
                double paired = score[i - 1, j - 1] + Of(anchor[i - 1], window[j - 1]);
                double skipAnchorLine = score[i - 1, j];
                double skipWindowLine = score[i, j - 1];
                score[i, j] = Math.Max(paired, Math.Max(skipAnchorLine, skipWindowLine));
            }
        }

        return score[anchor.Count, window.Count] / Math.Max(anchor.Count, window.Count);
    }
}
