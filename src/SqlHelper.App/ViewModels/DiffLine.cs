using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

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
    public bool IsChange => Kind != DiffLineKind.Unchanged;

    public string Prefix => Kind switch
    {
        DiffLineKind.Inserted => "+",
        DiffLineKind.Deleted => "-",
        _ => " ",
    };

    public string LineNumberText => LineNumber?.ToString() ?? string.Empty;
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
    public static IReadOnlyList<DiffLine> Build(string? before, string? after, bool ignoreWhitespace = false)
    {
        DiffPaneModel model = InlineDiffBuilder.Diff(before ?? string.Empty, after ?? string.Empty, ignoreWhitespace);

        var lines = new List<DiffLine>(model.Lines.Count);
        int changeIndex = -1;
        bool inChange = false;

        foreach (DiffPiece piece in model.Lines)
        {
            DiffLineKind kind = Map(piece.Type);

            if (kind == DiffLineKind.Unchanged)
            {
                inChange = false;
            }
            else if (!inChange)
            {
                // A run of adjacent changed lines counts as one block, which is what a reader
                // means by "the next change".
                inChange = true;
                changeIndex++;
            }

            lines.Add(new DiffLine(kind, piece.Position, piece.Text ?? string.Empty, kind == DiffLineKind.Unchanged ? -1 : changeIndex));
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

    private static DiffLineKind Map(ChangeType type) => type switch
    {
        ChangeType.Inserted => DiffLineKind.Inserted,
        ChangeType.Deleted => DiffLineKind.Deleted,
        ChangeType.Modified => DiffLineKind.Modified,
        _ => DiffLineKind.Unchanged,
    };
}
