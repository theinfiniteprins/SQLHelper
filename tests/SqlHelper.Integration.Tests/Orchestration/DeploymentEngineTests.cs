using SqlHelper.Core.Auditing;
using SqlHelper.Core.Model;
using SqlHelper.Core.Orchestration;
using SqlHelper.Core.Patching;
using SqlHelper.Core.Scripting;
using SqlHelper.Integration.Tests.Infrastructure;

namespace SqlHelper.Integration.Tests.Orchestration;

[Collection(SqlServerCollection.Name)]
public sealed class DeploymentEngineTests : IDisposable
{
    private readonly SqlServerFixture _sql;
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "sqlhelper-deploy-tests", Guid.NewGuid().ToString("N"));
    private readonly AuditLog _audit;
    private readonly DeploymentEngine _engine;

    public DeploymentEngineTests(SqlServerFixture sql)
    {
        _sql = sql;
        Directory.CreateDirectory(_workDir);
        _audit = new AuditLog(Path.Combine(_workDir, "audit"));
        _engine = new DeploymentEngine(_sql.Connections, _audit);
    }

    public void Dispose()
    {
        _audit.Dispose();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string BackupRoot => Path.Combine(_workDir, "backups");

    /// <summary>Backups carry a timestamp in the file name, so locate them by pattern.</summary>
    private static string OnlyFile(string sessionFolder, string client, string pattern)
    {
        string[] hits = Directory.GetFiles(Path.Combine(sessionFolder, client), pattern);
        Assert.Single(hits);
        return hits[0];
    }

    [Fact]
    public async Task DeployObjectAsync_replaces_the_object_on_every_client_and_backs_up_each_ones_own_prior_version()
    {
        DatabaseTarget acme = await _sql.CreateClientDatabaseAsync("Acme");
        DatabaseTarget globex = await _sql.CreateClientDatabaseAsync("Globex");

        await _sql.ExecOnAsync(acme, "CREATE PROCEDURE dbo.usp_GetOrders AS SELECT 1 AS AcmeVersion");
        await _sql.ExecOnAsync(globex, "CREATE PROCEDURE dbo.usp_GetOrders AS SELECT 1 AS GlobexVersion -- custom");

        const string newDefinition = "CREATE PROCEDURE dbo.usp_GetOrders AS SELECT 2 AS NewVersion";

        DeployResult result = await _engine.DeployObjectAsync(
            [acme, globex], ObjectName.Parse("dbo.usp_GetOrders"), newDefinition, BackupRoot, "Fix rounding", "ORD-1");

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);

        // Each client's own prior definition was captured, not a shared/blank one.
        string acmeBackup = await File.ReadAllTextAsync(OnlyFile(result.BackupFolder, "Acme", "dbo.usp_GetOrders_*.sql"));
        string globexBackup = await File.ReadAllTextAsync(OnlyFile(result.BackupFolder, "Globex", "dbo.usp_GetOrders_*.sql"));
        Assert.Contains("AcmeVersion", acmeBackup);
        Assert.Contains("GlobexVersion", globexBackup);

        // Rollback scripts exist and restore each client's own original.
        string acmeRollback = await File.ReadAllTextAsync(Path.Combine(result.BackupFolder, "_rollback", "rollback__Acme.sql"));
        Assert.Contains("AcmeVersion", acmeRollback);

        Assert.True(File.Exists(result.ManifestPath));
        string manifest = await File.ReadAllTextAsync(result.ManifestPath);
        Assert.Contains("\"succeeded\"", manifest);

        // The live databases actually changed.
        var inspector = new ObjectInspector();
        await using var acmeConn = await _sql.Connections.OpenAsync(acme, CancellationToken.None);
        ProgrammableObject? acmeNow = await inspector.GetAsync(acmeConn, ObjectName.Parse("dbo.usp_GetOrders"));
        Assert.Contains("NewVersion", acmeNow!.Definition);

        IReadOnlyList<AuditEvent> auditEvents = await _audit.ReadMonthAsync(DateTime.UtcNow.Year, DateTime.UtcNow.Month);
        Assert.Contains(auditEvents, e => e.Action == "deploy-object" && e.SucceededCount == 2);
    }

    [Fact]
    public async Task DeployObjectAsync_records_a_new_object_and_its_rollback_drops_it()
    {
        DatabaseTarget acme = await _sql.CreateClientDatabaseAsync("Acme");

        DeployResult result = await _engine.DeployObjectAsync(
            [acme], ObjectName.Parse("dbo.usp_BrandNew"), "CREATE PROCEDURE dbo.usp_BrandNew AS SELECT 1",
            BackupRoot, "New helper proc");

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal("new-object", result.Results[0].Method);

        string marker = await File.ReadAllTextAsync(OnlyFile(result.BackupFolder, "Acme", "dbo.usp_BrandNew_*.NEW.txt"));
        Assert.Contains("did not exist", marker, StringComparison.OrdinalIgnoreCase);

        string rollback = await File.ReadAllTextAsync(Path.Combine(result.BackupFolder, "_rollback", "rollback__Acme.sql"));
        Assert.Contains("DROP PROCEDURE IF EXISTS [dbo].[usp_BrandNew]", rollback);
    }

    [Fact]
    public async Task Full_cohort_to_patch_pipeline_handles_a_canonical_client_and_a_drifted_one()
    {
        DatabaseTarget canonicalClient = await _sql.CreateClientDatabaseAsync("Canonical-Co");
        DatabaseTarget driftedClient = await _sql.CreateClientDatabaseAsync("Drifted-Co");

        const string canonicalBefore =
            "CREATE PROCEDURE dbo.usp_GetOrders @id int AS\nBEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id\nEND";
        // A real statement (not just a comment, which the semantic normaliser would treat as
        // cosmetic) wedged between BEGIN and the target line — a genuine customisation that both
        // the cohort analysis and the exact-match tier must see as different.
        const string driftedBefore =
            "CREATE PROCEDURE dbo.usp_GetOrders @id int AS\n" +
            "BEGIN\n  IF @id IS NULL SET @id = -1;\n  SELECT * FROM dbo.Orders WHERE Id = @id\nEND";

        await _sql.ExecOnAsync(canonicalClient, canonicalBefore);
        await _sql.ExecOnAsync(driftedClient, driftedBefore);

        ObjectName objectName = ObjectName.Parse("dbo.usp_GetOrders");
        var targets = new[] { canonicalClient, driftedClient };

        // 1) Cohort analysis should see the drift before anything is touched.
        var captures = await _engine.CaptureForCohortAnalysisAsync(targets, objectName);
        CohortReport cohorts = CohortAnalyzer.Cluster(captures);
        Assert.Equal(2, cohorts.Cohorts.Count);

        // 2) Author the patch from a clean before/after reference pair.
        const string canonicalAfter =
            "CREATE PROCEDURE dbo.usp_GetOrders @id int AS\nBEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id AND Active = 1\nEND";
        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-1487", canonicalBefore, canonicalAfter);
        Assert.True(built.Ok, built.Error);

        // 3) Plan against both live databases.
        IReadOnlyList<PatchAttempt> attempts = await _engine.PlanPatchAsync(targets, objectName, built.Patch!);
        PatchAttempt forCanonical = attempts.Single(a => a.Target.ClientName == "Canonical-Co");
        PatchAttempt forDrifted = attempts.Single(a => a.Target.ClientName == "Drifted-Co");
        Assert.Equal(PatchOutcome.Applied, forCanonical.Outcome);
        Assert.Equal(PatchOutcome.NeedsReview, forDrifted.Outcome);

        // 4) Approve both and deploy.
        DeployResult deployed = await _engine.ApplyPatchAsync(attempts, objectName, BackupRoot, "ORD-1487 rollout", "ORD-1487");
        Assert.Equal(2, deployed.SucceededCount);

        var inspector = new ObjectInspector();
        await using var driftedConn = await _sql.Connections.OpenAsync(driftedClient, CancellationToken.None);
        ProgrammableObject? driftedNow = await inspector.GetAsync(driftedConn, objectName);
        Assert.Contains("AND Active = 1", driftedNow!.Definition);
        Assert.Contains("SET @id = -1", driftedNow.Definition); // untouched customisation survived

        string driftedRollback = await File.ReadAllTextAsync(Path.Combine(deployed.BackupFolder, "_rollback", "rollback__Drifted-Co.sql"));
        Assert.Contains("SET @id = -1", driftedRollback);

        // 5) Patching takes its own backup of each client's pre-patch version, named the same way
        //    as a deploy backup, and containing that client's own code — not the reference version.
        string driftedBackup = await File.ReadAllTextAsync(OnlyFile(deployed.BackupFolder, "Drifted-Co", "dbo.usp_GetOrders_*.sql"));
        Assert.Contains("SET @id = -1", driftedBackup);
        Assert.DoesNotContain("AND Active = 1", driftedBackup);

        string canonicalBackup = await File.ReadAllTextAsync(OnlyFile(deployed.BackupFolder, "Canonical-Co", "dbo.usp_GetOrders_*.sql"));
        Assert.DoesNotContain("SET @id = -1", canonicalBackup);
        Assert.DoesNotContain("AND Active = 1", canonicalBackup);
    }

    [Fact]
    public async Task ApplyPatchAsync_refuses_a_client_whose_code_changed_after_the_plan_was_built()
    {
        DatabaseTarget acme = await _sql.CreateClientDatabaseAsync("Acme");

        const string before = "CREATE PROCEDURE dbo.usp_GetOrders @id int AS\nBEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id\nEND";
        const string after = "CREATE PROCEDURE dbo.usp_GetOrders @id int AS\nBEGIN\n  SELECT * FROM dbo.Orders WHERE Id = @id AND Active = 1\nEND";

        await _sql.ExecOnAsync(acme, before);

        ObjectName objectName = ObjectName.Parse("dbo.usp_GetOrders");
        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-1", before, after);
        Assert.True(built.Ok, built.Error);

        IReadOnlyList<PatchAttempt> attempts = await _engine.PlanPatchAsync([acme], objectName, built.Patch!);
        Assert.Equal(PatchOutcome.Applied, attempts[0].Outcome);

        // Somebody else edits the same procedure between the plan and the deploy.
        await _sql.ExecOnAsync(acme,
            "ALTER PROCEDURE dbo.usp_GetOrders @id int AS\nBEGIN\n  SELECT TOP 10 * FROM dbo.Orders WHERE Id = @id\nEND");

        DeployResult deployed = await _engine.ApplyPatchAsync(attempts, objectName, BackupRoot, "ORD-1 rollout", "ORD-1");

        Assert.Equal(0, deployed.SucceededCount);
        Assert.Equal(1, deployed.FailedCount);
        Assert.Contains("changed after the plan was built", deployed.Results[0].Error, StringComparison.Ordinal);

        // The live procedure is exactly what the other person left, untouched by us.
        var inspector = new ObjectInspector();
        await using var conn = await _sql.Connections.OpenAsync(acme, CancellationToken.None);
        ProgrammableObject? now = await inspector.GetAsync(conn, objectName);
        Assert.Contains("SELECT TOP 10", now!.Definition);
        Assert.DoesNotContain("AND Active = 1", now.Definition);
    }
}
