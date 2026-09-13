using SqlHelper.App.ViewModels;

namespace SqlHelper.App.Tests;

/// <summary>Each test here is one of the problems reported from using the comparison panes.</summary>
public sealed class DiffReviewReportedIssuesTests
{
    // ------------------------------------------------------------------ "ignore whitespace" showed an added tab

    [Fact]
    public void An_extra_tab_in_the_middle_of_a_line_is_not_a_difference_when_ignoring_whitespace()
    {
        const string before = "BEGIN\n\t\tAND\t\t[dbo].[T].[IsDeleted] = 0\nEND";
        const string after = "BEGIN\n\t\tAND\t\t\t[dbo].[T].[IsDeleted] = 0\nEND";

        Assert.Equal(1, DiffRenderer.Summarise(DiffRenderer.Build(before, after, ignoreWhitespace: false)).ChangeBlocks);
        Assert.Equal(0, DiffRenderer.Summarise(DiffRenderer.Build(before, after, ignoreWhitespace: true)).ChangeBlocks);
    }

    [Fact]
    public void Whitespace_inside_a_string_is_still_a_difference_when_ignoring_whitespace()
    {
        const string before = "SET @s = 'a b'";
        const string after = "SET @s = 'a  b'";

        Assert.Equal(1, DiffRenderer.Summarise(DiffRenderer.Build(before, after, ignoreWhitespace: true)).ChangeBlocks);
    }

    [Fact]
    public void Ticking_ignore_whitespace_in_the_panel_hides_a_reindented_block()
    {
        var vm = new DiffReviewViewModel();
        vm.SetContent("SELECT a,\n    b,\n    c\nFROM t", "SELECT a,\n\tb,\n\t\tc\nFROM t");
        Assert.True(vm.HasChanges);

        vm.IgnoreWhitespace = true;

        Assert.False(vm.HasChanges);
    }

    // ------------------------------------------------------------------ "next change" did nothing

    private static DiffReviewViewModel LongWithOneChangeAtTheEnd()
    {
        string before = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"SELECT {i}"));
        string after = before + "\nSELECT 'the change'";
        var vm = new DiffReviewViewModel();
        vm.SetContent(before, after);
        return vm;
    }

    [Fact]
    public void From_the_top_next_goes_to_a_change_at_the_very_end()
    {
        DiffReviewViewModel vm = LongWithOneChangeAtTheEnd();

        // The reader has scrolled back to the top: lines 0-30 are on screen.
        vm.TopVisibleLine = 0;
        vm.BottomVisibleLine = 30;
        ScrollRequest? before = vm.ScrollRequest;

        vm.NextChangeCommand.Execute(null);

        Assert.NotNull(vm.ScrollRequest);
        Assert.NotSame(before, vm.ScrollRequest);
        Assert.True(vm.ScrollRequest!.LineIndex >= 190, $"expected a jump to the end, got line {vm.ScrollRequest.LineIndex}");
        Assert.Equal("Change 1 of 1", vm.PositionText);
    }

    [Fact]
    public void Pressing_next_again_on_the_only_change_still_scrolls_to_it()
    {
        DiffReviewViewModel vm = LongWithOneChangeAtTheEnd();

        vm.NextChangeCommand.Execute(null);
        ScrollRequest first = vm.ScrollRequest!;

        vm.NextChangeCommand.Execute(null);

        // Same line, but a new request - so the view really moves even if it had been scrolled away.
        Assert.NotSame(first, vm.ScrollRequest);
        Assert.Equal(first.LineIndex, vm.ScrollRequest!.LineIndex);
    }

    [Fact]
    public void Next_walks_through_every_change_in_turn_while_each_is_on_screen()
    {
        string before = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"SELECT {i}"));
        string after = before.Replace("SELECT 10\n", "SELECT 10\nNEW A\n", StringComparison.Ordinal)
                             .Replace("SELECT 50\n", "SELECT 50\nNEW B\n", StringComparison.Ordinal)
                             .Replace("SELECT 90\n", "SELECT 90\nNEW C\n", StringComparison.Ordinal);
        var vm = new DiffReviewViewModel();
        vm.SetContent(before, after);
        Assert.Equal(3, vm.Stats.ChangeBlocks);

        for (int expected = 1; expected <= 3; expected++)
        {
            // Wherever the last jump put the view, that change is on screen.
            int top = vm.ScrollRequest!.LineIndex;
            vm.TopVisibleLine = top;
            vm.BottomVisibleLine = top + 20;

            Assert.Equal($"Change {expected} of 3", vm.PositionText);
            vm.NextChangeCommand.Execute(null);
        }

        Assert.Equal("Change 1 of 3", vm.PositionText); // wrapped round
    }

    [Fact]
    public void After_scrolling_by_hand_next_goes_to_the_first_change_below_the_view_not_the_one_after_the_last_jump()
    {
        string before = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"SELECT {i}"));
        string after = before.Replace("SELECT 10\n", "SELECT 10\nNEW A\n", StringComparison.Ordinal)
                             .Replace("SELECT 50\n", "SELECT 50\nNEW B\n", StringComparison.Ordinal)
                             .Replace("SELECT 90\n", "SELECT 90\nNEW C\n", StringComparison.Ordinal);
        var vm = new DiffReviewViewModel();
        vm.SetContent(before, after); // on change 1

        // The reader scrolls down past change 2 by hand.
        vm.TopVisibleLine = 60;
        vm.BottomVisibleLine = 80;
        vm.NextChangeCommand.Execute(null);

        Assert.Equal("Change 3 of 3", vm.PositionText);
    }

    [Fact]
    public void After_scrolling_by_hand_previous_goes_to_the_last_change_above_the_view()
    {
        string before = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"SELECT {i}"));
        string after = before.Replace("SELECT 10\n", "SELECT 10\nNEW A\n", StringComparison.Ordinal)
                             .Replace("SELECT 50\n", "SELECT 50\nNEW B\n", StringComparison.Ordinal)
                             .Replace("SELECT 90\n", "SELECT 90\nNEW C\n", StringComparison.Ordinal);
        var vm = new DiffReviewViewModel();
        vm.SetContent(before, after);

        vm.TopVisibleLine = 70;
        vm.BottomVisibleLine = 85;
        vm.PreviousChangeCommand.Execute(null);

        Assert.Equal("Change 2 of 3", vm.PositionText);
    }

    // ------------------------------------------------------------------ editing must not make the view jump

    [Fact]
    public void Refreshing_while_someone_types_does_not_jump_the_view()
    {
        var vm = new DiffReviewViewModel();
        vm.SetContent("a\nb\nc", "a\nB\nc");
        ScrollRequest? afterLoad = vm.ScrollRequest;

        vm.Refresh("a\nb\nc", "a\nBB\nc");

        Assert.Same(afterLoad, vm.ScrollRequest);
        Assert.Equal(1, vm.Stats.ChangeBlocks);
    }

    // ------------------------------------------------------------------ "not showing properly"

    [Fact]
    public void Tabs_are_expanded_to_four_column_stops_for_display_but_kept_exactly_for_copying()
    {
        var line = new DiffLine(DiffLineKind.Unchanged, 1, "\tAND\t\tx = 1", -1);

        Assert.Equal("    AND     x = 1", line.DisplayText);
        Assert.Equal("\tAND\t\tx = 1", line.Text);
    }

    [Fact]
    public void A_several_line_change_is_shown_as_one_block_not_scattered_across_brackets()
    {
        const string before = "WHERE (\n    x = 1\n)\nAND (\n    y = 2\n)";
        const string after = "WHERE (\n    x = 1\n    OR x = 3\n    OR x = 4\n)\nAND (\n    y = 2\n)";

        IReadOnlyList<DiffLine> lines = DiffRenderer.Build(before, after);

        Assert.Equal(1, DiffRenderer.Summarise(lines).ChangeBlocks);
        Assert.Equal(["    OR x = 3", "    OR x = 4"], lines.Where(l => l.IsChange).Select(l => l.Text));
    }

    [Fact]
    public void The_copy_sources_are_the_exact_texts_that_were_compared()
    {
        var vm = new DiffReviewViewModel();
        vm.SetContent("old\ttext", "new\ttext");

        Assert.Equal("old\ttext", vm.BeforeText);
        Assert.Equal("new\ttext", vm.AfterText);
    }
}
