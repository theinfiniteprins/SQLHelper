using Microsoft.Data.SqlClient;
using SqlHelper.Core.Backup;
using SqlHelper.Core.Guard;
using SqlHelper.Core.Model;
using SqlHelper.Core.Patching;
using SqlHelper.Core.Scripting;
using SqlHelper.Integration.Tests.Infrastructure;

namespace SqlHelper.Integration.Tests.Patching;

/// <summary>
/// The full loop against a real server: capture a live definition, author a patch from a
/// before/after reference pair, apply it through the planner, push the result back with
/// <c>ALTER</c>, and confirm the database now reflects it.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PatchingEndToEndTests
{
    private readonly SqlServerFixture _sql;
    private readonly ObjectInspector _inspector = new();

    public PatchingEndToEndTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task A_patch_authored_from_reference_scripts_applies_to_the_live_definition()
    {
        const string original =
            """
            CREATE PROCEDURE dbo.usp_Patch_GetOrders @id int AS
            BEGIN
                SELECT * FROM dbo.Orders WHERE Id = @id
            END
            """;
        await _sql.ExecAsync(original);

        // The operator authors the change from two full reference versions — this is the
        // natural shape of the input: an SSMS "script as ALTER" header on each, including a USE
        // for a *different* database than the one being patched, which must not leak into the patch.
        string before = "USE [SomeOtherDb]\r\nGO\r\nSET ANSI_NULLS ON\r\nGO\r\n" + original + "\r\nGO\r\n";
        string after = before.Replace(
            "SELECT * FROM dbo.Orders WHERE Id = @id",
            "SELECT * FROM dbo.Orders WHERE Id = @id AND Active = 1",
            StringComparison.Ordinal);

        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-1", before, after);
        Assert.True(built.Ok, built.Error);

        await using SqlConnection connection = await _sql.Connections.OpenAsync(_sql.Target, CancellationToken.None);
        ProgrammableObject? current = await _inspector.GetAsync(connection, ObjectName.Parse("dbo.usp_Patch_GetOrders"));
        Assert.NotNull(current);

        PatchAttempt attempt = PatchPlanner.Plan(_sql.Target, current, built.Patch!);
        Assert.Equal(PatchOutcome.Applied, attempt.Outcome);
        Assert.True(attempt.IsCleanExactMatch);

        // Deploy the patched body exactly as the real engine would: CREATE OR ALTER, correct SET options.
        var patchedModule = current with { Definition = attempt.PatchedBody! };
        string runnable = ModuleScript.ToRunnableScript(patchedModule);
        foreach (ScriptBatch batch in GoBatchSplitter.Split(runnable))
        {
            await using SqlCommand cmd = connection.CreateCommand();
            cmd.CommandText = batch.Text;
            await cmd.ExecuteNonQueryAsync();
        }

        ProgrammableObject? after2 = await _inspector.GetAsync(connection, ObjectName.Parse("dbo.usp_Patch_GetOrders"));
        Assert.Contains("AND Active = 1", after2!.Definition);
        Assert.DoesNotContain("SomeOtherDb", after2.Definition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cohort_analysis_reflects_real_drift_across_several_databases()
    {
        await _sql.ExecAsync("CREATE PROCEDURE dbo.usp_Patch_Cohort AS SELECT 1 AS canonical");

        await using SqlConnection connection = await _sql.Connections.OpenAsync(_sql.Target, CancellationToken.None);
        ProgrammableObject? captured = await _inspector.GetAsync(connection, ObjectName.Parse("dbo.usp_Patch_Cohort"));

        var captures = new[]
        {
            new ObjectCapture(Fake("Acme"), captured),
            new ObjectCapture(Fake("Globex"), captured),
            new ObjectCapture(Fake("Initech"), captured! with { Definition = "CREATE PROCEDURE dbo.usp_Patch_Cohort AS SELECT 2 AS custom" }),
        };

        CohortReport report = CohortAnalyzer.Cluster(captures);

        Assert.Equal(2, report.Cohorts.Count);
        Assert.Equal(2, report.Largest!.Count);
    }

    private static DatabaseTarget Fake(string client) => new() { ClientName = client, ServerName = "s", DatabaseName = "d" };
}
