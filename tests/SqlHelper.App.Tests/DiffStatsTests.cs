using SqlHelper.App.ViewModels;

namespace SqlHelper.App.Tests;

/// <summary>
/// The summary line above a diff is the first thing a reader trusts, so it has to describe the
/// change the way a person would count it.
/// </summary>
public sealed class DiffStatsTests
{
    private static DiffStats Stats(string before, string after, bool ignoreWhitespace = false) =>
        DiffRenderer.Summarise(DiffRenderer.Build(before, after, ignoreWhitespace));

    [Fact]
    public void An_edited_line_counts_once_as_changed_not_twice()
    {
        DiffStats stats = Stats("a\nb\nc", "a\nB\nc");

        Assert.Equal(1, stats.Modified);
        Assert.Equal(0, stats.Added);
        Assert.Equal(0, stats.Removed);
        Assert.Equal(2, stats.Unchanged);
        Assert.Equal(1, stats.ChangeBlocks);
        Assert.Equal(1, stats.TotalChangedLines);
    }

    [Fact]
    public void A_purely_added_line_is_reported_as_added()
    {
        DiffStats stats = Stats("a\nc", "a\nb\nc");

        Assert.Equal(0, stats.Modified);
        Assert.Equal(1, stats.Added);
        Assert.Equal(0, stats.Removed);
    }

    [Fact]
    public void A_purely_removed_line_is_reported_as_removed()
    {
        DiffStats stats = Stats("a\nb\nc", "a\nc");

        Assert.Equal(0, stats.Modified);
        Assert.Equal(0, stats.Added);
        Assert.Equal(1, stats.Removed);
    }

    [Fact]
    public void A_block_that_both_edits_and_adds_splits_the_counts()
    {
        // One line rewritten, one genuinely new line, in the same run.
        DiffStats stats = Stats("a\nb\nz", "a\nB\nNEW\nz");

        Assert.Equal(1, stats.Modified);
        Assert.Equal(1, stats.Added);
        Assert.Equal(0, stats.Removed);
        Assert.Equal(1, stats.ChangeBlocks);
    }

    [Fact]
    public void Two_separate_edits_are_two_places()
    {
        DiffStats stats = Stats("a\nb\nc\nd\ne", "A\nb\nc\nd\nE");

        Assert.Equal(2, stats.Modified);
        Assert.Equal(2, stats.ChangeBlocks);
        Assert.Contains("in 2 places", stats.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Identical_text_says_there_are_no_differences()
    {
        DiffStats stats = Stats("a\nb", "a\nb");

        Assert.Equal(0, stats.ChangeBlocks);
        Assert.Equal("No differences", stats.Summary);
    }

    [Fact]
    public void Ignoring_whitespace_makes_an_indentation_only_change_disappear()
    {
        Assert.NotEqual(0, Stats("a\n  b", "a\n      b", ignoreWhitespace: false).ChangeBlocks);
        Assert.Equal(0, Stats("a\n  b", "a\n      b", ignoreWhitespace: true).ChangeBlocks);
    }

    [Fact]
    public void The_summary_only_mentions_the_kinds_that_actually_occur()
    {
        string summary = Stats("a\nb\nc", "a\nB\nc").Summary;

        Assert.Contains("1 line(s) changed", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("added", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("removed", summary, StringComparison.Ordinal);
    }
}
