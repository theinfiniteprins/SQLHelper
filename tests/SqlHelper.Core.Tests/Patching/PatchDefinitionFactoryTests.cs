using SqlHelper.Core.Patching;

namespace SqlHelper.Core.Tests.Patching;

public sealed class PatchDefinitionFactoryTests
{
    private const string BeforeBody =
        "CREATE PROCEDURE dbo.usp_GetOrders @id int AS\nBEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id\nEND";

    private const string AfterBody =
        "CREATE PROCEDURE dbo.usp_GetOrders @id int AS\nBEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id AND Active = 1\nEND";

    [Fact]
    public void Builds_a_hunk_from_two_clean_versions()
    {
        PatchDefinitionResult result = PatchDefinitionFactory.FromReferenceDiff("ORD-1487", BeforeBody, AfterBody);

        Assert.True(result.Ok, result.Error);
        PatchHunk hunk = Assert.Single(result.Patch!.Hunks);
        Assert.Contains("Id = @id", hunk.AnchorText);
        Assert.Contains("AND Active = 1", hunk.ReplacementText);
    }

    [Fact]
    public void Strips_a_USE_GO_SET_preamble_from_both_sides_before_diffing()
    {
        string before = "USE [Acme]\nGO\nSET ANSI_NULLS ON\nGO\nSET QUOTED_IDENTIFIER ON\nGO\n" + BeforeBody + "\nGO\n";
        // Different database name in the preamble — must not show up as part of the change.
        string after = "USE [Globex]\nGO\nSET ANSI_NULLS ON\nGO\nSET QUOTED_IDENTIFIER ON\nGO\n" + AfterBody + "\nGO\n";

        PatchDefinitionResult result = PatchDefinitionFactory.FromReferenceDiff("ORD-1487", before, after);

        Assert.True(result.Ok, result.Error);
        PatchHunk hunk = Assert.Single(result.Patch!.Hunks);
        Assert.DoesNotContain("USE", hunk.AnchorText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Globex", hunk.ReplacementText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AND Active = 1", hunk.ReplacementText);
    }

    [Fact]
    public void Includes_surrounding_context_lines()
    {
        PatchDefinitionResult result = PatchDefinitionFactory.FromReferenceDiff("t", BeforeBody, AfterBody, contextLines: 1);

        PatchHunk hunk = result.Patch!.Hunks[0];
        Assert.Contains("BEGIN", hunk.AnchorText);
        Assert.Contains("END", hunk.AnchorText);
    }

    [Fact]
    public void Identical_before_and_after_is_rejected()
    {
        PatchDefinitionResult result = PatchDefinitionFactory.FromReferenceDiff("t", BeforeBody, BeforeBody);

        Assert.False(result.Ok);
        Assert.Contains("identical", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mismatched_object_names_are_rejected()
    {
        string otherAfter = AfterBody.Replace("usp_GetOrders", "usp_GetInvoices", StringComparison.Ordinal);

        PatchDefinitionResult result = PatchDefinitionFactory.FromReferenceDiff("t", BeforeBody, otherAfter);

        Assert.False(result.Ok);
        Assert.Contains("same object", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unparseable_before_script_is_rejected_with_a_clear_reason()
    {
        PatchDefinitionResult result = PatchDefinitionFactory.FromReferenceDiff("t", "SELECT FROM WHERE", AfterBody);

        Assert.False(result.Ok);
        Assert.Contains("'Before' script", result.Error);
    }

    [Fact]
    public void Multiple_separate_changes_produce_multiple_hunks()
    {
        string before = "CREATE PROCEDURE dbo.x @id int AS\nBEGIN\n  DECLARE @a int = 1\n  SELECT @a\n  -- gap\n  DECLARE @b int = 2\n  SELECT @b\nEND";
        string after = "CREATE PROCEDURE dbo.x @id int AS\nBEGIN\n  DECLARE @a int = 100\n  SELECT @a\n  -- gap\n  DECLARE @b int = 200\n  SELECT @b\nEND";

        PatchDefinitionResult result = PatchDefinitionFactory.FromReferenceDiff("t", before, after, contextLines: 0);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.Patch!.Hunks.Count);
    }
}
