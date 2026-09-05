using SqlHelper.Core.Guard;

namespace SqlHelper.Core.Tests.Guard;

public sealed class GoBatchSplitterTests
{
    [Fact]
    public void No_GO_yields_a_single_batch()
    {
        IReadOnlyList<ScriptBatch> batches = GoBatchSplitter.Split("SELECT 1\nSELECT 2");

        Assert.Single(batches);
        Assert.Equal("SELECT 1\nSELECT 2", batches[0].Text);
        Assert.Equal(1, batches[0].RepeatCount);
    }

    [Fact]
    public void GO_on_its_own_line_separates_batches()
    {
        IReadOnlyList<ScriptBatch> batches = GoBatchSplitter.Split("SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3\n");

        Assert.Equal(["SELECT 1", "SELECT 2", "SELECT 3"], batches.Select(b => b.Text));
    }

    [Fact]
    public void Lowercase_go_is_still_a_separator()
    {
        Assert.Equal(2, GoBatchSplitter.Split("select 1\ngo\nselect 2").Count);
    }

    [Fact]
    public void GO_inside_a_string_literal_is_not_a_separator()
    {
        IReadOnlyList<ScriptBatch> batches = GoBatchSplitter.Split("SELECT 'line1\nGO\nline2'");

        Assert.Single(batches);
    }

    [Fact]
    public void GO_inside_a_block_comment_is_not_a_separator()
    {
        IReadOnlyList<ScriptBatch> batches = GoBatchSplitter.Split("SELECT 1 /* a\nGO\nb */\nSELECT 2");

        Assert.Single(batches);
    }

    [Fact]
    public void GOTO_is_not_mistaken_for_a_batch_separator()
    {
        const string script = "WHILE 1=1\nBEGIN\n  IF 1=1 GOTO done\nEND\ndone:\nSELECT 1";

        Assert.Single(GoBatchSplitter.Split(script));
    }

    [Fact]
    public void GO_with_a_count_is_captured()
    {
        IReadOnlyList<ScriptBatch> batches = GoBatchSplitter.Split("INSERT INTO log DEFAULT VALUES\nGO 5\nSELECT 1");

        Assert.Equal(5, batches[0].RepeatCount);
        Assert.Equal(1, batches[1].RepeatCount);
    }

    [Fact]
    public void Leading_and_trailing_and_blank_batches_are_dropped()
    {
        IReadOnlyList<ScriptBatch> batches = GoBatchSplitter.Split("GO\n\nSELECT 1\nGO\n   \nGO\nSELECT 2\nGO\n");

        Assert.Equal(["SELECT 1", "SELECT 2"], batches.Select(b => b.Text));
    }

    [Fact]
    public void Empty_input_yields_no_batches()
    {
        Assert.Empty(GoBatchSplitter.Split(string.Empty));
    }

    [Fact]
    public void Start_line_points_at_the_batch_in_the_original_script()
    {
        IReadOnlyList<ScriptBatch> batches = GoBatchSplitter.Split("SELECT 1\nGO\nSELECT 2\nSELECT 3\nGO\nSELECT 4");

        Assert.Equal(1, batches[0].StartLine);
        Assert.Equal(3, batches[1].StartLine);
        Assert.Equal(6, batches[2].StartLine);
    }

    [Fact]
    public void Handles_crlf_line_endings()
    {
        IReadOnlyList<ScriptBatch> batches = GoBatchSplitter.Split("SELECT 1\r\nGO\r\nSELECT 2\r\n");

        Assert.Equal(["SELECT 1", "SELECT 2"], batches.Select(b => b.Text));
    }

    [Fact]
    public void An_inline_GO_after_a_statement_on_the_same_line_is_not_a_separator()
    {
        // "GO" is only a batch separator when it is the first token on a line.
        IReadOnlyList<ScriptBatch> batches = GoBatchSplitter.Split("SELECT 1 GO\nSELECT 2");

        Assert.Single(batches);
    }
}
