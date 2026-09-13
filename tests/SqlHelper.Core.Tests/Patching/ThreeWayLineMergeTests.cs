using SqlHelper.Core.Patching;

namespace SqlHelper.Core.Tests.Patching;

/// <summary>
/// The merge is where a change meets a client's own copy. These tests build the base, the change
/// and the client's drift independently, so the correct merged result is known in advance, and
/// then check the merge produces exactly that — or refuses. Producing anything else is the one
/// outcome that is never acceptable.
/// </summary>
public sealed class ThreeWayLineMergeTests
{
    private static List<RawLine> Client(IEnumerable<string> lines) => [.. lines.Select(l => new RawLine(l, "\n"))];

    private static string[] Texts(MergeResult result) => [.. result.Merged!.Select(l => l.Text)];

    // ------------------------------------------------------------------ the basic rules

    [Fact]
    public void An_identical_client_gets_exactly_the_change()
    {
        string[] baseLines = ["BEGIN", "    SELECT 1", "END"];
        string[] ours = ["BEGIN", "    SELECT 1", "    SELECT 2", "END"];

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(baseLines), "\n");

        Assert.True(result.Ok, result.Conflict);
        Assert.Equal(ours, Texts(result));
    }

    [Fact]
    public void A_client_line_added_away_from_the_change_is_kept()
    {
        string[] baseLines = ["A", "B", "C", "D", "E"];
        string[] ours = ["A", "B", "C", "D", "NEW", "E"];
        string[] client = ["A", "CLIENT", "B", "C", "D", "E"];

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");

        Assert.True(result.Ok, result.Conflict);
        Assert.Equal(["A", "CLIENT", "B", "C", "D", "NEW", "E"], Texts(result));
    }

    [Fact]
    public void The_clients_own_formatting_survives_on_every_line_the_change_does_not_touch()
    {
        string[] baseLines = ["SELECT a, b", "FROM dbo.T", "WHERE x = 1"];
        string[] ours = ["SELECT a, b", "FROM dbo.T", "WHERE x = 1", "AND y = 2"];
        string[] client = ["\tSELECT\ta,b", "\t\tFROM   dbo.T", "WHERE x=1"];

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");

        Assert.True(result.Ok, result.Conflict);
        Assert.Equal(["\tSELECT\ta,b", "\t\tFROM   dbo.T", "WHERE x=1", "AND y = 2"], Texts(result));
    }

    [Fact]
    public void A_client_that_rewrote_the_same_line_is_a_conflict()
    {
        string[] baseLines = ["A", "x = 1", "B"];
        string[] ours = ["A", "x = 2", "B"];
        string[] client = ["A", "x = 3", "B"];

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");

        Assert.False(result.Ok);
        Assert.Null(result.Merged);
    }

    [Fact]
    public void A_client_that_already_made_the_same_change_is_left_as_it_is()
    {
        string[] baseLines = ["A", "x = 1", "B"];
        string[] ours = ["A", "x = 2", "B"];
        string[] client = ["A", "x=2", "B"];

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");

        Assert.True(result.Ok, result.Conflict);
        Assert.Equal(["A", "x=2", "B"], Texts(result));
    }

    [Fact]
    public void Inserting_into_a_run_of_repeated_lines_the_client_also_changed_is_refused_as_ambiguous()
    {
        // The client has an extra "X" and the change goes between two "X"s: whether the new line
        // belongs before or after the client's extra copy cannot be told from the text.
        string[] baseLines = ["X", "X"];
        string[] ours = ["X", "NEW", "X"];
        string[] client = ["X", "X", "X"];

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");

        Assert.False(result.Ok);
    }

    [Fact]
    public void Whitespace_inside_a_literal_is_a_real_difference_not_formatting()
    {
        // The client's literal differs, but a stable line separates it from what the change
        // rewrites - so it is kept exactly as the client has it, and the change still lands.
        string[] baseLines = ["SET @s = 'a b'", "DECLARE @n int", "SELECT 1"];
        string[] ours = ["SET @s = 'a b'", "DECLARE @n int", "SELECT 2"];
        string[] client = ["SET @s = 'a  b'", "DECLARE @n int", "SELECT 1"];

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");

        Assert.True(result.Ok, result.Conflict);
        Assert.Equal(["SET @s = 'a  b'", "DECLARE @n int", "SELECT 2"], Texts(result));
    }

    [Fact]
    public void A_client_difference_right_next_to_the_changed_line_is_a_conflict_just_as_in_version_control()
    {
        // No unchanged line between the client's difference and the change: there is no way to be
        // sure the two edits do not interact, so it goes to a hand edit.
        string[] baseLines = ["SET @s = 'a b'", "SELECT 1"];
        string[] ours = ["SET @s = 'a b'", "SELECT 2"];
        string[] client = ["SET @s = 'a  b'", "SELECT 1"];

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");

        Assert.False(result.Ok);
    }

    [Fact]
    public void Line_endings_slot_back_into_the_client_exactly()
    {
        string[] baseLines = ["A", "B"];
        string[] ours = ["A", "B", "C"];
        List<RawLine> client = [new("A", "\r\n"), new("B", "")]; // B is the last line of the object

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, client, "\r\n");

        Assert.True(result.Ok, result.Conflict);
        Assert.Equal([new RawLine("A", "\r\n"), new RawLine("B", "\r\n"), new RawLine("C", "")], result.Merged);
    }

    // ------------------------------------------------------------------ client lines right beside a change

    [Fact]
    public void A_comment_the_client_wrote_directly_above_the_changed_line_is_kept_above_it()
    {
        string[] baseLines = ["BEGIN", "    SELECT * FROM dbo.T WHERE Id = @id", "END"];
        string[] ours = ["BEGIN", "    SELECT * FROM dbo.T WHERE Id = @id AND Active = 1", "END"];
        string[] client = ["BEGIN", "    -- client: log first", "    SELECT * FROM dbo.T WHERE Id = @id", "END"];

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");

        Assert.True(result.Ok, result.Conflict);
        Assert.Equal(
            ["BEGIN", "    -- client: log first", "    SELECT * FROM dbo.T WHERE Id = @id AND Active = 1", "END"],
            Texts(result));
    }

    [Fact]
    public void A_line_the_client_added_directly_below_the_changed_line_is_kept_below_it()
    {
        string[] baseLines = ["A", "x = 1", "B"];
        string[] ours = ["A", "x = 2", "B"];
        string[] client = ["A", "x = 1", "-- client note", "B"];

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");

        Assert.True(result.Ok, result.Conflict);
        Assert.Equal(["A", "x = 2", "-- client note", "B"], Texts(result));
    }

    [Fact]
    public void A_client_line_inside_a_span_the_change_rewrites_is_a_conflict()
    {
        // The change collapses two lines into one; the client put a line between them. Those two
        // lines no longer exist, so there is nowhere certain to put the client's line.
        string[] baseLines = ["A", "x = 1", "y = 1", "B"];
        string[] ours = ["A", "x = 1, y = 1", "B"];
        string[] client = ["A", "x = 1", "-- between", "y = 1", "B"];

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");

        Assert.False(result.Ok);
    }

    [Fact]
    public void The_client_and_the_change_both_adding_at_the_same_spot_is_a_conflict()
    {
        string[] baseLines = ["A", "B"];
        string[] ours = ["A", "OURS", "B"];
        string[] client = ["A", "THEIRS", "B"];

        MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");

        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(202)]
    public void Client_lines_added_right_beside_a_change_merge_to_exactly_the_right_order_or_are_refused(int seed)
    {
        var random = new Random(seed);
        int merged = 0;

        for (int n = 0; n < 2000; n++)
        {
            int count = 3 + random.Next(10);
            string[] baseLines = [.. Enumerable.Range(0, count).Select(i => $"S{n}_{i}")];
            int target = random.Next(count);

            // The change rewrites one line.
            string[] ours = [.. baseLines];
            ours[target] = $"CHANGED_{n}";

            // The client adds a line immediately above or below that same line.
            bool above = random.Next(2) == 0;
            var client = new List<string>(baseLines);
            client.Insert(above ? target : target + 1, $"CLIENT_{n}");

            var expected = new List<string>(ours);
            expected.Insert(above ? target : target + 1, $"CLIENT_{n}");

            MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");
            if (!result.Ok)
            {
                continue;
            }

            merged++;
            Assert.Equal(expected, Texts(result));
        }

        Assert.True(merged > 1900, $"expected nearly all of these to merge, got {merged}");
    }

    // ------------------------------------------------------------------ ground truth, at volume

    /// <summary>
    /// Builds a base of distinctive lines separated by runs of common structural lines, then
    /// applies the change and the client's drift to <em>different</em> distinctive lines, so the
    /// correct merge is simply both edits applied.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(22)]
    [InlineData(33)]
    public void Non_overlapping_edits_always_merge_to_exactly_both_changes_or_are_refused(int seed)
    {
        var random = new Random(seed);
        string[] filler = ["(", ")", "AND", "END", "", "BEGIN", "OR"];

        int merged = 0;
        int refused = 0;

        for (int n = 0; n < 1500; n++)
        {
            // Segments: each a distinctive line followed by some structural filler.
            int segments = 4 + random.Next(8);
            var baseSegments = new List<List<string>>();
            for (int s = 0; s < segments; s++)
            {
                var segment = new List<string> { $"STMT_{n}_{s}" };
                int fill = random.Next(4);
                for (int f = 0; f < fill; f++)
                {
                    segment.Add(filler[random.Next(filler.Length)]);
                }

                baseSegments.Add(segment);
            }

            // Ours edits one segment, theirs another, never the same one or adjacent ones.
            int oursSeg = random.Next(segments);
            int theirsSeg;
            do
            {
                theirsSeg = random.Next(segments);
            }
            while (Math.Abs(theirsSeg - oursSeg) < 2);

            List<List<string>> oursSegments = Clone(baseSegments);
            List<List<string>> theirsSegments = Clone(baseSegments);
            List<List<string>> expectedSegments = Clone(baseSegments);

            ApplyEdit(random, oursSegments[oursSeg], expectedSegments[oursSeg], $"OURS_{n}");
            ApplyEdit(random, theirsSegments[theirsSeg], expectedSegments[theirsSeg], $"THEIRS_{n}");

            string[] baseLines = [.. baseSegments.SelectMany(x => x)];
            string[] oursLines = [.. oursSegments.SelectMany(x => x)];
            string[] clientLines = [.. theirsSegments.SelectMany(x => x)];
            string[] expected = [.. expectedSegments.SelectMany(x => x)];

            MergeResult result = ThreeWayLineMerge.Merge(baseLines, oursLines, Client(clientLines), "\n");

            if (!result.Ok)
            {
                refused++;
                continue;
            }

            merged++;
            Assert.True(
                expected.SequenceEqual(Texts(result)),
                $"seed {seed}, case {n}: merged result is not the two edits applied.\n" +
                $"base:     {string.Join(" | ", baseLines)}\nours:     {string.Join(" | ", oursLines)}\n" +
                $"client:   {string.Join(" | ", clientLines)}\nexpected: {string.Join(" | ", expected)}\n" +
                $"actual:   {string.Join(" | ", Texts(result))}");
        }

        // Refusing is safe, but with distinctive separators it should be rare.
        Assert.True(merged > refused * 9, $"merged {merged}, refused {refused}");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void Edits_to_the_same_line_are_never_silently_resolved(int seed)
    {
        var random = new Random(seed);

        for (int n = 0; n < 1500; n++)
        {
            int count = 3 + random.Next(10);
            string[] baseLines = [.. Enumerable.Range(0, count).Select(i => $"L{n}_{i}")];
            int target = random.Next(count);

            string[] ours = [.. baseLines];
            ours[target] = "OURS";
            string[] client = [.. baseLines];
            client[target] = "THEIRS";

            MergeResult result = ThreeWayLineMerge.Merge(baseLines, ours, Client(client), "\n");

            Assert.False(result.Ok, $"case {n}: two different rewrites of line {target} must conflict");
        }
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void A_client_differing_only_in_formatting_always_gets_the_change_with_its_formatting_kept(int seed)
    {
        var random = new Random(seed);

        for (int n = 0; n < 1500; n++)
        {
            int count = 4 + random.Next(12);
            string[] baseLines = [.. Enumerable.Range(0, count).Select(i => $"SELECT [c{i}] FROM dbo.T{n} WHERE x = {i}")];
            string[] client = [.. baseLines.Select(l => Reformat(random, l))];

            int at = random.Next(count + 1);
            List<string> ours = [.. baseLines];
            ours.Insert(at, $"-- added {n}");

            List<string> expected = [.. client];
            expected.Insert(at, $"-- added {n}");

            MergeResult result = ThreeWayLineMerge.Merge(baseLines, [.. ours], Client(client), "\n");

            Assert.True(result.Ok, $"case {n}: {result.Conflict}");
            Assert.Equal(expected, Texts(result));
        }
    }

    private static string Reformat(Random random, string line) => random.Next(3) switch
    {
        0 => "\t" + line.Replace(" = ", "=", StringComparison.Ordinal),
        1 => line.Replace(" ", "\t", StringComparison.Ordinal),
        _ => "    " + line,
    };

    private static List<List<string>> Clone(List<List<string>> segments) => [.. segments.Select(s => new List<string>(s))];

    /// <summary>One edit to a segment's distinctive line: insert after it, replace it, or delete a filler line.</summary>
    private static void ApplyEdit(Random random, List<string> edited, List<string> expected, string marker)
    {
        switch (random.Next(3))
        {
            case 0:
                edited.Insert(1, marker);
                expected.Insert(1, marker);
                break;
            case 1:
                edited[0] = edited[0] + "_" + marker;
                expected[0] = expected[0] + "_" + marker;
                break;
            default:
                edited.Insert(0, marker);
                expected.Insert(0, marker);
                break;
        }
    }
}
