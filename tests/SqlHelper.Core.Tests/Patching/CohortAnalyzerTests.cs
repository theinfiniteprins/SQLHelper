using SqlHelper.Core.Model;
using SqlHelper.Core.Patching;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Tests.Patching;

public sealed class CohortAnalyzerTests
{
    private static readonly ObjectName Name = new("dbo", "usp_X");

    private static DatabaseTarget Client(string name) => new() { ClientName = name, ServerName = "s", DatabaseName = "d" };

    private static ProgrammableObject Module(string definition) => new(
        Name, ProgrammableObjectKind.StoredProcedure, definition,
        true, true, 1, DateTime.UtcNow, DateTime.UtcNow, IsEncrypted: false);

    [Fact]
    public void Groups_by_logic_ignoring_cosmetic_differences()
    {
        var captures = new[]
        {
            new ObjectCapture(Client("A"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 1")),
            new ObjectCapture(Client("B"), Module("create   procedure dbo.usp_X as   select   1")),
            new ObjectCapture(Client("C"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 2")),
        };

        CohortReport report = CohortAnalyzer.Cluster(captures);

        Assert.Equal(2, report.Cohorts.Count);
        Cohort majority = report.Largest!;
        Assert.Equal(2, majority.Count);
        Assert.Equal(["A", "B"], majority.Members.Select(t => t.ClientName));
        Assert.Equal("Reference (most common)", majority.Label);
    }

    [Fact]
    public void Exact_key_mode_treats_whitespace_only_differences_as_distinct()
    {
        var captures = new[]
        {
            new ObjectCapture(Client("A"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 1")),
            new ObjectCapture(Client("B"), Module("create   procedure dbo.usp_X as   select   1")),
        };

        CohortReport report = CohortAnalyzer.Cluster(captures, useSemanticKey: false);

        Assert.Equal(2, report.Cohorts.Count);
    }

    [Fact]
    public void Missing_and_encrypted_objects_get_their_own_labelled_cohorts()
    {
        var captures = new[]
        {
            new ObjectCapture(Client("A"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 1")),
            new ObjectCapture(Client("Missing"), null),
            new ObjectCapture(Client("Locked"), new ProgrammableObject(
                Name, ProgrammableObjectKind.StoredProcedure, "", true, true, 1,
                DateTime.UtcNow, DateTime.UtcNow, IsEncrypted: true)),
        };

        CohortReport report = CohortAnalyzer.Cluster(captures);

        Assert.Contains(report.Cohorts, c => c.Label == "Object does not exist" && c.Members.Single().ClientName == "Missing");
        Assert.Contains(report.Cohorts, c => c.Label == "Encrypted — cannot inspect" && c.Members.Single().ClientName == "Locked");
    }

    [Fact]
    public void A_database_that_could_not_be_read_is_never_reported_as_missing_the_object()
    {
        // Reporting a failed connection as "object does not exist" would send someone off to
        // create an object that is already there.
        var captures = new[]
        {
            new ObjectCapture(Client("A"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 1")),
            new ObjectCapture(Client("Unreachable"), null, "Login failed for user 'app_user'."),
            new ObjectCapture(Client("GenuinelyMissing"), null),
        };

        CohortReport report = CohortAnalyzer.Cluster(captures);

        Cohort failed = Assert.Single(report.Cohorts, c => c.IsFailure);
        Assert.Equal("Unreachable", failed.Members.Single().ClientName);
        Assert.Contains("Login failed", failed.Error);
        Assert.Equal("Could not be read", failed.Label);

        Cohort missing = Assert.Single(report.Cohorts, c => c.Label == "Object does not exist");
        Assert.Equal("GenuinelyMissing", missing.Members.Single().ClientName);
    }

    [Fact]
    public void A_failed_database_is_never_treated_as_the_reference_version()
    {
        var captures = new[]
        {
            new ObjectCapture(Client("Broken1"), null, "timeout"),
            new ObjectCapture(Client("Broken2"), null, "timeout"),
            new ObjectCapture(Client("Good"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 1")),
        };

        CohortReport report = CohortAnalyzer.Cluster(captures);

        Cohort reference = Assert.Single(report.Cohorts, c => c.Label == "Reference (most common)");
        Assert.Equal("Good", reference.Members.Single().ClientName);
    }

    [Fact]
    public void All_identical_reports_a_single_cohort()
    {
        var captures = new[]
        {
            new ObjectCapture(Client("A"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 1")),
            new ObjectCapture(Client("B"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 1")),
        };

        CohortReport report = CohortAnalyzer.Cluster(captures);

        Assert.True(report.AllIdentical);
    }

    [Fact]
    public void Minority_variants_are_lettered_starting_at_B()
    {
        var captures = new[]
        {
            new ObjectCapture(Client("A1"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 1")),
            new ObjectCapture(Client("A2"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 1")),
            new ObjectCapture(Client("A3"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 1")),
            new ObjectCapture(Client("B1"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 2")),
            new ObjectCapture(Client("C1"), Module("CREATE PROCEDURE dbo.usp_X AS SELECT 3")),
        };

        CohortReport report = CohortAnalyzer.Cluster(captures);

        Assert.Equal(["Reference (most common)", "Variant B", "Variant C"], report.Cohorts.Select(c => c.Label));
    }
}
