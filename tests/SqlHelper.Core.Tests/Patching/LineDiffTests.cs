using SqlHelper.Core.Patching;

namespace SqlHelper.Core.Tests.Patching;

public sealed class LineDiffTests
{
    private static string[] Apply(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, IReadOnlyList<LineDiffOp> ops)
    {
        var rebuilt = new List<string>();
        foreach (LineDiffOp op in ops)
        {
            if (op.Kind == LineDiffKind.Equal)
            {
                for (int k = 0; k < op.Count; k++)
                {
                    rebuilt.Add(oldLines[op.OldStart + k]);
                }
            }
            else if (op.Kind == LineDiffKind.Insert)
            {
                for (int k = 0; k < op.Count; k++)
                {
                    rebuilt.Add(newLines[op.NewStart + k]);
                }
            }
        }

        return [.. rebuilt];
    }

    // ------------------------------------------------------------------ correctness

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_diff_rebuilds_the_new_text_exactly(int seed)
    {
        var random = new Random(seed);

        for (int n = 0; n < 3000; n++)
        {
            // A tiny alphabet means lots of repeated lines - the hard case for alignment.
            int alphabet = 1 + random.Next(6);
            string[] oldLines = [.. Enumerable.Range(0, random.Next(40)).Select(_ => "L" + random.Next(alphabet))];
            List<string> edited = [.. oldLines];

            int edits = random.Next(6);
            for (int e = 0; e < edits; e++)
            {
                int at = edited.Count == 0 ? 0 : random.Next(edited.Count + 1);
                switch (random.Next(3))
                {
                    case 0:
                        edited.Insert(Math.Min(at, edited.Count), "L" + random.Next(alphabet + 2));
                        break;
                    case 1 when edited.Count > 0:
                        edited.RemoveAt(Math.Min(at, edited.Count - 1));
                        break;
                    case 2 when edited.Count > 0:
                        edited[Math.Min(at, edited.Count - 1)] = "L" + random.Next(alphabet + 2);
                        break;
                }
            }

            string[] newLines = [.. edited];
            IReadOnlyList<LineDiffOp> ops = LineDiff.Compute(oldLines, newLines);

            Assert.True(LineDiff.IsValid(oldLines, newLines, ops), $"invalid diff at iteration {n}");
            Assert.Equal(newLines, Apply(oldLines, newLines, ops));
        }
    }

    [Fact]
    public void Identical_inputs_produce_a_single_equal_run()
    {
        string[] lines = ["a", "b", "c"];
        IReadOnlyList<LineDiffOp> ops = LineDiff.Compute(lines, lines);

        LineDiffOp op = Assert.Single(ops);
        Assert.Equal(LineDiffKind.Equal, op.Kind);
        Assert.Equal(3, op.Count);
    }

    [Fact]
    public void Empty_inputs_are_handled()
    {
        Assert.Empty(LineDiff.Compute([], []));
        Assert.Equal(LineDiffKind.Insert, Assert.Single(LineDiff.Compute([], ["a"])).Kind);
        Assert.Equal(LineDiffKind.Delete, Assert.Single(LineDiff.Compute(["a"], [])).Kind);
        Assert.Equal(LineDiffKind.Insert, Assert.Single(LineDiff.Compute([], [""])).Kind);
    }

    [Fact]
    public void Removals_come_before_additions_within_a_change()
    {
        IReadOnlyList<LineDiffOp> ops = LineDiff.Compute(["x", "old1", "old2", "y"], ["x", "new1", "new2", "new3", "y"]);

        Assert.Equal(
            [LineDiffKind.Equal, LineDiffKind.Delete, LineDiffKind.Insert, LineDiffKind.Equal],
            ops.Select(o => o.Kind));
    }

    [Fact]
    public void A_very_large_input_with_no_unique_lines_still_diffs_correctly()
    {
        // Big enough to exceed the LCS table budget and hand over to DiffPlex.
        string[] oldLines = [.. Enumerable.Range(0, 3000).Select(i => i % 2 == 0 ? "(" : ")")];
        string[] newLines = [.. oldLines.Take(1500).Append("NEW").Concat(oldLines.Skip(1500))];

        IReadOnlyList<LineDiffOp> ops = LineDiff.Compute(oldLines, newLines);

        Assert.True(LineDiff.IsValid(oldLines, newLines, ops));
        Assert.Equal(1, ops.Where(o => o.Kind == LineDiffKind.Insert).Sum(o => o.Count));
    }

    // ------------------------------------------------------------------ readability

    [Fact]
    public void A_multi_line_change_stays_in_one_piece_instead_of_scattering_over_brackets()
    {
        // Structural lines repeat constantly; the change should still read as one block.
        string[] oldLines =
        [
            "SELECT [A]", "FROM dbo.T", "WHERE (", "    x = 1", ")", "AND (", "    y = 2", ")", "ORDER BY [A]",
        ];
        string[] newLines =
        [
            "SELECT [A]", "FROM dbo.T", "WHERE (", "    x = 1", "    OR x = 3", ")", "AND (", "    y = 2", "    AND z = 4", ")", "ORDER BY [A]",
        ];

        IReadOnlyList<LineDiffOp> ops = LineDiff.Compute(oldLines, newLines);

        Assert.True(LineDiff.IsValid(oldLines, newLines, ops));
        Assert.Equal(0, ops.Where(o => o.Kind == LineDiffKind.Delete).Sum(o => o.Count));
        Assert.Equal(2, ops.Count(o => o.Kind == LineDiffKind.Insert));
    }

    [Fact]
    public void Distinctive_lines_anchor_the_alignment()
    {
        // A shortest-edit diff may pair "END" with the wrong "END"; unique lines prevent that.
        string[] oldLines = ["BEGIN", "SELECT 1", "END", "BEGIN", "SELECT 2", "END"];
        string[] newLines = ["BEGIN", "SELECT 1", "END", "BEGIN", "SELECT 99", "SELECT 2", "END"];

        IReadOnlyList<LineDiffOp> ops = LineDiff.Compute(oldLines, newLines);

        LineDiffOp insert = Assert.Single(ops, o => o.Kind == LineDiffKind.Insert);
        Assert.Equal(4, insert.NewStart);
        Assert.Equal(1, insert.Count);
    }
}
