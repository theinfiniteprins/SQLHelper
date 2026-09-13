using System.Text;
using SqlHelper.Core.Patching;

namespace SqlHelper.App.ViewModels;

public enum DiffLineKind
{
    Unchanged,
    Inserted,
    Deleted,
    Modified,
}

/// <param name="LineNumber">Line number in the resulting text; null for a removed line.</param>
/// <param name="ChangeIndex">Which change block this line belongs to; -1 for unchanged lines.</param>
public sealed record DiffLine(DiffLineKind Kind, int? LineNumber, string Text, int ChangeIndex)
{
    /// <summary>Tab stops every four columns, the way SSMS shows them, so indentation lines up.</summary>
    public const int TabSize = 4;

    public bool IsChange => Kind != DiffLineKind.Unchanged;

    public string Prefix => Kind switch
    {
        DiffLineKind.Inserted => "+",
        DiffLineKind.Deleted => "-",
        _ => " ",
    };

    public string LineNumberText => LineNumber?.ToString() ?? string.Empty;

    /// <summary>
    /// The text as it should look on screen. A tab rendered by WPF jumps to a stop measured in
    /// device units rather than columns, so a tab-indented procedure never lined up the way it
    /// does in SSMS. Tabs are expanded to spaces for display only; <see cref="Text"/> stays exact
    /// for copying.
    /// </summary>
    public string DisplayText { get; } = ExpandTabs(Text);

    private static string ExpandTabs(string text)
    {
        if (!text.Contains('\t', StringComparison.Ordinal))
        {
            return text;
        }

        var expanded = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            if (c == '\t')
            {
                expanded.Append(' ', TabSize - (expanded.Length % TabSize));
            }
            else
            {
                expanded.Append(c);
            }
        }

        return expanded.ToString();
    }
}

/// <summary>
/// How much actually changed, for the summary above a diff.
///
/// A line-based diff only knows "removed" and "added", so a line that was edited shows up as one
/// of each. Reading that as two separate changes overstates the size of the change, so within a
/// block the removals and additions are paired up: the pairs are counted as <see cref="Modified"/>
/// and only the leftovers are reported as purely added or purely removed.
/// </summary>
public sealed record DiffStats(int Modified, int Added, int Removed, int Unchanged, int ChangeBlocks)
{
    public static DiffStats Empty { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Every line the reader has to look at, counting an edited line once.</summary>
    public int TotalChangedLines => Modified + Added + Removed;

    public string Summary
    {
        get
        {
            if (ChangeBlocks == 0)
            {
                return "No differences";
            }

            var parts = new List<string>(4);
            if (Modified > 0)
            {
                parts.Add($"{Modified} line(s) changed");
            }

            if (Added > 0)
            {
                parts.Add($"{Added} added");
            }

            if (Removed > 0)
            {
                parts.Add($"{Removed} removed");
            }

            parts.Add(ChangeBlocks == 1 ? "in 1 place" : $"in {ChangeBlocks} places");
            parts.Add($"{Unchanged} line(s) untouched");
            return string.Join("  ·  ", parts);
        }
    }
}

public static class DiffRenderer
{
    /// <summary>
    /// Lines of <paramref name="before"/> and <paramref name="after"/> laid out as one inline diff.
    ///
    /// Alignment uses <see cref="LineDiff"/>, which anchors on distinctive lines, so a change of
    /// several lines reads as one block rather than being scattered across every <c>(</c> and
    /// <c>END</c> that happens to match. With <paramref name="ignoreWhitespace"/> on, lines are
    /// compared by <see cref="SqlLineKeys"/>: a tab added in the middle of a line is formatting and
    /// disappears, whitespace inside a string literal is content and still shows.
    /// </summary>
    public static IReadOnlyList<DiffLine> Build(string? before, string? after, bool ignoreWhitespace = false)
    {
        string[] oldLines = SplitLines(before);
        string[] newLines = SplitLines(after);

        IReadOnlyList<LineDiffOp> ops = ignoreWhitespace
            ? LineDiff.Compute(SqlLineKeys.Compute(oldLines), SqlLineKeys.Compute(newLines))
            : LineDiff.Compute(oldLines, newLines);

        var lines = new List<DiffLine>(Math.Max(oldLines.Length, newLines.Length) + 8);
        int changeIndex = -1;
        bool inChange = false;

        foreach (LineDiffOp op in ops)
        {
            if (op.Kind == LineDiffKind.Equal)
            {
                inChange = false;
                for (int k = 0; k < op.Count; k++)
                {
                    // The new side's text: with whitespace ignored the two may differ in formatting.
                    lines.Add(new DiffLine(DiffLineKind.Unchanged, op.NewStart + k + 1, newLines[op.NewStart + k], -1));
                }

                continue;
            }

            if (!inChange)
            {
                // A run of adjacent changed lines counts as one block, which is what a reader
                // means by "the next change".
                inChange = true;
                changeIndex++;
            }

            for (int k = 0; k < op.Count; k++)
            {
                lines.Add(op.Kind == LineDiffKind.Delete
                    ? new DiffLine(DiffLineKind.Deleted, null, oldLines[op.OldStart + k], changeIndex)
                    : new DiffLine(DiffLineKind.Inserted, op.NewStart + k + 1, newLines[op.NewStart + k], changeIndex));
            }
        }

        return lines;
    }

    public static DiffStats Summarise(IReadOnlyList<DiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        int unchanged = lines.Count(l => l.Kind == DiffLineKind.Unchanged);
        int modified = 0;
        int added = 0;
        int removed = 0;
        int blocks = 0;

        foreach (IGrouping<int, DiffLine> block in lines.Where(l => l.IsChange).GroupBy(l => l.ChangeIndex))
        {
            blocks++;
            int blockAdded = block.Count(l => l.Kind == DiffLineKind.Inserted);
            int blockRemoved = block.Count(l => l.Kind == DiffLineKind.Deleted);

            // A removed line and an added line in the same place is one line being edited.
            int paired = Math.Min(blockAdded, blockRemoved);
            modified += paired + block.Count(l => l.Kind == DiffLineKind.Modified);
            added += blockAdded - paired;
            removed += blockRemoved - paired;
        }

        return new DiffStats(modified, added, removed, unchanged, blocks);
    }

    /// <summary>Splits on any line ending, keeping every line including a final empty one.</summary>
    internal static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }
}
