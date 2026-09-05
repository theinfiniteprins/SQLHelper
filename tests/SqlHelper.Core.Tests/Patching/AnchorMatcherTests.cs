using SqlHelper.Core.Patching;

namespace SqlHelper.Core.Tests.Patching;

public sealed class AnchorMatcherTests
{
    [Fact]
    public void Applies_an_exact_unique_match()
    {
        string target = "BEGIN\n  SELECT * FROM Orders WHERE Id = @id\nEND";
        string anchor = "SELECT * FROM Orders WHERE Id = @id";
        string replacement = "SELECT * FROM Orders WHERE Id = @id AND Active = 1";

        AnchorMatchResult result = AnchorMatcher.TryApply(target, anchor, replacement);

        Assert.Equal(AnchorMatchStatus.Unique, result.Status);
        Assert.Contains("AND Active = 1", result.PatchedText);
        Assert.StartsWith("BEGIN\n", result.PatchedText);
        Assert.EndsWith("END", result.PatchedText);
    }

    [Fact]
    public void Matches_despite_different_indentation()
    {
        string target = "BEGIN\n\t\tSELECT * FROM Orders WHERE Id = @id\nEND";
        string anchor = "  SELECT * FROM Orders WHERE Id = @id"; // reference used two-space indent

        AnchorMatchResult result = AnchorMatcher.TryApply(target, anchor, "  SELECT 1");

        Assert.Equal(AnchorMatchStatus.Unique, result.Status);
    }

    [Fact]
    public void Reports_not_found_when_the_client_has_diverged()
    {
        string target = "BEGIN\n  SELECT * FROM Orders WHERE Status = 'open'\nEND";
        string anchor = "SELECT * FROM Orders WHERE Id = @id";

        AnchorMatchResult result = AnchorMatcher.TryApply(target, anchor, "x");

        Assert.Equal(AnchorMatchStatus.NotFound, result.Status);
        Assert.Null(result.PatchedText);
    }

    [Fact]
    public void Reports_ambiguous_when_the_anchor_appears_twice()
    {
        string target = "SELECT 1\nSELECT 1\n";
        AnchorMatchResult result = AnchorMatcher.TryApply(target, "SELECT 1", "SELECT 2");

        Assert.Equal(AnchorMatchStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.MatchCount);
        Assert.Null(result.PatchedText);
    }

    [Fact]
    public void A_multi_line_anchor_with_context_locates_the_right_block()
    {
        string target =
            "CREATE PROCEDURE dbo.x AS\nBEGIN\n  DECLARE @x int\n  SELECT @x\n  SELECT @x + 1\nEND";
        string anchor = "  DECLARE @x int\n  SELECT @x";
        string replacement = "  DECLARE @x int = 0\n  SELECT @x";

        AnchorMatchResult result = AnchorMatcher.TryApply(target, anchor, replacement);

        Assert.Equal(AnchorMatchStatus.Unique, result.Status);
        Assert.Contains("DECLARE @x int = 0", result.PatchedText);
        Assert.Contains("SELECT @x + 1", result.PatchedText); // untouched line survives
    }

    [Fact]
    public void Replacement_lines_take_on_the_targets_line_ending_style()
    {
        string target = "BEGIN\r\n  SELECT 1\r\nEND"; // CRLF target
        string replacement = "  SELECT 2\n  SELECT 3\n"; // authored with LF

        AnchorMatchResult result = AnchorMatcher.TryApply(target, "  SELECT 1", replacement);

        Assert.Equal(AnchorMatchStatus.Unique, result.Status);
        Assert.Contains("SELECT 2\r\n  SELECT 3\r\nEND", result.PatchedText);
    }

    [Fact]
    public void An_anchor_at_the_very_start_of_the_text_matches()
    {
        string target = "SELECT 1\nSELECT 2\n";
        AnchorMatchResult result = AnchorMatcher.TryApply(target, "SELECT 1", "SELECT 10");

        Assert.Equal(AnchorMatchStatus.Unique, result.Status);
        Assert.StartsWith("SELECT 10", result.PatchedText);
    }

    [Fact]
    public void An_anchor_at_the_very_end_of_the_text_matches()
    {
        string target = "SELECT 1\nSELECT 2";
        AnchorMatchResult result = AnchorMatcher.TryApply(target, "SELECT 2", "SELECT 20");

        Assert.Equal(AnchorMatchStatus.Unique, result.Status);
        Assert.EndsWith("SELECT 20", result.PatchedText);
    }

    [Fact]
    public void Unrelated_customisation_elsewhere_in_the_file_does_not_block_the_match()
    {
        string target =
            "CREATE PROCEDURE dbo.x @id int AS\n" +
            "-- Acme custom: rate-limit check added 2024\n" +
            "IF @id < 0 RETURN;\n" +
            "BEGIN\n  SELECT * FROM Orders WHERE Id = @id\nEND";

        AnchorMatchResult result = AnchorMatcher.TryApply(
            target, "SELECT * FROM Orders WHERE Id = @id", "SELECT * FROM Orders WHERE Id = @id AND Active = 1");

        Assert.Equal(AnchorMatchStatus.Unique, result.Status);
        Assert.Contains("Acme custom", result.PatchedText); // untouched, preserved
        Assert.Contains("AND Active = 1", result.PatchedText);
    }
}
