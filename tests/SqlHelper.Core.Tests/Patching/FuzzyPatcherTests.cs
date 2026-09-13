using System.Text;
using SqlHelper.Core.Patching;

namespace SqlHelper.Core.Tests.Patching;

public sealed class FuzzyPatcherTests
{
    private const string Anchor =
        "  SELECT o.Id, o.Total\n  FROM dbo.Orders o\n  WHERE o.CreatedOn BETWEEN @from AND @to";

    private const string Replacement =
        "  SELECT o.Id, o.Total\n  FROM dbo.Orders o\n  WHERE o.CreatedOn BETWEEN @from AND @to AND o.Active = 1";

    /// <summary>A realistic procedure where the interesting lines are a long way from the top.</summary>
    private static string LongProcedure(int paddingLines = 60)
    {
        var text = new StringBuilder();
        text.AppendLine("CREATE PROCEDURE dbo.usp_BigReport @from date, @to date AS");
        text.AppendLine("BEGIN");
        text.AppendLine("  SET NOCOUNT ON;");
        for (int i = 0; i < paddingLines; i++)
        {
            text.AppendLine($"  DECLARE @filler{i} int = {i};");
        }

        text.AppendLine("  SELECT o.Id, o.Total");
        text.AppendLine("  FROM dbo.Orders o");
        text.AppendLine("  WHERE o.CreatedOn BETWEEN @from AND @to");
        text.AppendLine("END");
        return text.ToString();
    }

    [Fact]
    public void Finds_the_block_however_far_down_the_procedure_it_sits()
    {
        // The previous matcher searched near where the change sat inside the anchor snippet, so
        // anything more than a few hundred characters down the file was unreachable and every
        // client fell through to "manual edit needed".
        FuzzyPatchResult result = FuzzyPatcher.TryApply(LongProcedure(200), Anchor, Replacement);

        Assert.True(result.Applied, result.Reason);
        Assert.Contains("o.Active = 1", result.PatchedText);
        Assert.True(result.MatchLine > 200, $"expected a match deep in the file, got line {result.MatchLine}");
    }

    [Fact]
    public void One_changed_character_near_the_change_still_matches()
    {
        // A character edited on a line the change does not rewrite: merge cleanly, keep their edit.
        string drifted = LongProcedure().Replace("SELECT o.Id, o.Total", "SELECT o.Id, o.TOTAL", StringComparison.Ordinal);

        FuzzyPatchResult result = FuzzyPatcher.TryApply(drifted, Anchor, Replacement);

        Assert.True(result.Applied, result.Reason);
        Assert.Contains("o.Active = 1", result.PatchedText);
        Assert.Contains("o.TOTAL", result.PatchedText);
        Assert.True(result.Confidence > 0.9, $"confidence was only {result.Confidence:P0}");
    }

    [Fact]
    public void A_client_edit_on_the_very_line_being_rewritten_is_reported_as_a_conflict()
    {
        // Taking the change here would throw away whatever the client put on that line - which on
        // a production database could be an extra WHERE clause. Refuse and say what clashed.
        string drifted = LongProcedure().Replace(
            "WHERE o.CreatedOn BETWEEN @from AND @to",
            "WHERE o.CreatedOn BETWEEN @from AND @to AND o.TenantId = 42",
            StringComparison.Ordinal);

        FuzzyPatchResult result = FuzzyPatcher.TryApply(drifted, Anchor, Replacement);

        Assert.False(result.Applied);
        Assert.Null(result.PatchedText);
        Assert.Contains("own version", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TenantId", result.Reason);
    }

    [Fact]
    public void A_comment_inserted_into_the_block_still_matches()
    {
        string drifted = LongProcedure().Replace(
            "  FROM dbo.Orders o",
            "  -- Acme: read from the archive view instead\n  FROM dbo.Orders o",
            StringComparison.Ordinal);

        FuzzyPatchResult result = FuzzyPatcher.TryApply(drifted, Anchor, Replacement);

        Assert.True(result.Applied, result.Reason);
        Assert.Contains("o.Active = 1", result.PatchedText);
    }

    [Fact]
    public void A_client_that_renamed_the_alias_on_the_line_being_changed_is_refused_not_mangled()
    {
        // The change rewrites "WHERE o.CreatedOn ..." but this client calls the alias "ord". There is
        // no correct text to produce: keeping the client's line drops the change, taking the
        // reference line introduces an alias that does not exist in this client's query. The earlier
        // merge produced both WHERE lines, one after the other. The only safe answer is a hand edit.
        string drifted = LongProcedure()
            .Replace("SELECT o.Id, o.Total", "SELECT ord.Id, ord.Total", StringComparison.Ordinal)
            .Replace("FROM dbo.Orders o", "FROM dbo.Orders ord", StringComparison.Ordinal)
            .Replace("WHERE o.CreatedOn", "WHERE ord.CreatedOn", StringComparison.Ordinal);

        FuzzyPatchResult result = FuzzyPatcher.TryApply(drifted, Anchor, Replacement);

        Assert.False(result.Applied);
        Assert.Null(result.PatchedText);
        Assert.Contains("own version", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrelated_code_is_refused_rather_than_forced()
    {
        string unrelated =
            "CREATE PROCEDURE dbo.usp_Unrelated AS\nBEGIN\n  UPDATE dbo.Invoices SET Paid = 1\n  DELETE FROM dbo.Audit\nEND";

        FuzzyPatchResult result = FuzzyPatcher.TryApply(unrelated, Anchor, Replacement);

        Assert.False(result.Applied);
        Assert.Null(result.PatchedText);
        Assert.Contains("similar", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_equally_good_candidates_are_refused_as_ambiguous()
    {
        // The same block twice: there is no way to tell which one the change belongs to.
        string twice =
            "CREATE PROCEDURE dbo.usp_Twice AS\nBEGIN\n" +
            "  SELECT o.Id, o.Total\n  FROM dbo.Orders o\n  WHERE o.CreatedOn BETWEEN @from AND @to\n" +
            "  UNION ALL\n" +
            "  SELECT o.Id, o.Total\n  FROM dbo.Orders o\n  WHERE o.CreatedOn BETWEEN @from AND @to\n" +
            "END";

        FuzzyPatchResult result = FuzzyPatcher.TryApply(twice, Anchor, Replacement);

        Assert.False(result.Applied);
        Assert.Null(result.PatchedText);
        Assert.Contains("not clear which one", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_trivially_short_anchor_is_refused()
    {
        FuzzyPatchResult result = FuzzyPatcher.TryApply("SELECT 1\nSELECT 2\n", "SELECT 1", "SELECT 100");

        Assert.False(result.Applied);
        Assert.Contains("too small", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Surrounding_code_is_carried_through_untouched()
    {
        string drifted = LongProcedure().Replace(
            "  SET NOCOUNT ON;",
            "  SET NOCOUNT ON;\n  -- Acme keeps its own audit hook here\n  EXEC dbo.usp_AcmeAudit;",
            StringComparison.Ordinal);

        FuzzyPatchResult result = FuzzyPatcher.TryApply(drifted, Anchor, Replacement);

        Assert.True(result.Applied, result.Reason);
        Assert.Contains("usp_AcmeAudit", result.PatchedText);
        Assert.Contains("@filler59", result.PatchedText);
    }

    [Fact]
    public void The_reason_names_the_line_it_matched_so_a_reviewer_can_check_it()
    {
        FuzzyPatchResult result = FuzzyPatcher.TryApply(LongProcedure(), Anchor, Replacement);

        Assert.True(result.Applied);
        Assert.Contains($"line {result.MatchLine}", result.Reason);
    }
}
