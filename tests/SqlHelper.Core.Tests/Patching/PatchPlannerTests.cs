using SqlHelper.Core.Model;
using SqlHelper.Core.Patching;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Patching;

public sealed class PatchPlannerTests
{
    private static readonly ObjectName Name = new("dbo", "usp_GetOrders");

    private static DatabaseTarget Target(string client = "Acme") => new()
    {
        ClientName = client, ServerName = "sql-01", DatabaseName = "AcmeDb",
    };

    private static ProgrammableObject Module(string definition) => new(
        Name, ProgrammableObjectKind.StoredProcedure, definition,
        UsesAnsiNulls: true, UsesQuotedIdentifier: true, ObjectId: 1,
        CreatedServerTime: DateTime.UtcNow, ModifiedServerTime: DateTime.UtcNow, IsEncrypted: false);

    private const string Canonical =
        "CREATE PROCEDURE dbo.usp_GetOrders @id int AS\nBEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id\nEND";

    private static PatchDefinition SimplePatch() => PatchDefinition.SingleHunk(
        "ORD-1487",
        Name,
        "SELECT * FROM dbo.Orders WHERE Id = @id",
        "SELECT * FROM dbo.Orders WHERE Id = @id AND Active = 1");

    [Fact]
    public void A_canonical_target_gets_a_clean_exact_match()
    {
        PatchAttempt attempt = PatchPlanner.Plan(Target(), Module(Canonical), SimplePatch());

        Assert.Equal(PatchOutcome.Applied, attempt.Outcome);
        Assert.True(attempt.IsCleanExactMatch);
        Assert.Contains("AND Active = 1", attempt.PatchedBody);
    }

    [Fact]
    public void Unrelated_customisation_elsewhere_still_applies_cleanly()
    {
        string clientBody =
            "CREATE PROCEDURE dbo.usp_GetOrders @id int AS\n" +
            "-- Acme custom: audit log\n" +
            "INSERT INTO dbo.AuditLog(Msg) VALUES ('called')\n" +
            "BEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id\nEND";

        PatchAttempt attempt = PatchPlanner.Plan(Target(), Module(clientBody), SimplePatch());

        Assert.Equal(PatchOutcome.Applied, attempt.Outcome);
        Assert.Contains("Acme custom", attempt.PatchedBody);
        Assert.Contains("AND Active = 1", attempt.PatchedBody);
    }

    [Fact]
    public void Drift_right_at_the_anchor_falls_back_to_fuzzy_and_needs_review()
    {
        // The anchor spans BEGIN through the SELECT line; a comment wedged between them breaks
        // the exact contiguous-line match but is close enough for the fuzzy tier to still find.
        var patch = PatchDefinition.SingleHunk(
            "ORD-1487",
            Name,
            "BEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id\nEND",
            "BEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id AND Active = 1\nEND");

        string clientBody =
            "CREATE PROCEDURE dbo.usp_GetOrders @id int AS\n" +
            "BEGIN\n-- Acme: log then fetch\n  SELECT * FROM dbo.Orders WHERE Id = @id\nEND";

        PatchAttempt attempt = PatchPlanner.Plan(Target(), Module(clientBody), patch);

        Assert.Equal(PatchOutcome.NeedsReview, attempt.Outcome);
        Assert.Contains(attempt.HunkAttempts, h => h.Tier == PatchTier.FuzzyAnchor && h.Applied);
        Assert.Contains("AND Active = 1", attempt.PatchedBody);
        Assert.Contains("Acme: log then fetch", attempt.PatchedBody);
    }

    [Fact]
    public void A_target_with_no_recognisable_match_needs_a_manual_edit()
    {
        string clientBody = "CREATE PROCEDURE dbo.usp_GetOrders @id int AS\nSELECT COUNT(*) FROM dbo.Invoices";

        PatchAttempt attempt = PatchPlanner.Plan(Target(), Module(clientBody), SimplePatch());

        Assert.Equal(PatchOutcome.ManualRequired, attempt.Outcome);
        Assert.Null(attempt.PatchedBody);
    }

    [Fact]
    public void A_target_that_already_has_the_fix_is_reported_as_already_current()
    {
        string clientBody =
            "CREATE PROCEDURE dbo.usp_GetOrders @id int AS\nBEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id AND Active = 1\nEND";

        PatchAttempt attempt = PatchPlanner.Plan(Target(), Module(clientBody), SimplePatch());

        Assert.Equal(PatchOutcome.AlreadyCurrent, attempt.Outcome);
    }

    [Fact]
    public void When_a_later_hunk_fails_no_hunk_is_partially_applied()
    {
        var patch = new PatchDefinition("t", Name,
        [
            new PatchHunk("SELECT * FROM dbo.Orders WHERE Id = @id", "SELECT * FROM dbo.Orders WHERE Id = @id AND Active = 1", 0),
            new PatchHunk("this text does not exist anywhere in the body", "replacement", 0),
        ]);

        PatchAttempt attempt = PatchPlanner.Plan(Target(), Module(Canonical), patch);

        Assert.Equal(PatchOutcome.ManualRequired, attempt.Outcome);
        Assert.Null(attempt.PatchedBody);
        // The first hunk must not have silently gone through even though it matched.
        Assert.DoesNotContain("AND Active = 1", attempt.OriginalBody);
    }

    [Fact]
    public void A_hunk_that_would_rename_the_object_fails_validation_and_is_never_offered()
    {
        var badPatch = PatchDefinition.SingleHunk(
            "t", Name,
            "CREATE PROCEDURE dbo.usp_GetOrders @id int AS",
            "CREATE PROCEDURE dbo.usp_GetOrdersRenamed @id int AS");

        PatchAttempt attempt = PatchPlanner.Plan(Target(), Module(Canonical), badPatch);

        Assert.Equal(PatchOutcome.ValidationFailed, attempt.Outcome);
    }
}
