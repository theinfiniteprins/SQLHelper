using SqlHelper.Core.Auditing;
using SqlHelper.Core.Model;
using SqlHelper.Core.Orchestration;
using SqlHelper.Core.Patching;
using SqlHelper.Core.Scripting;
using SqlHelper.Integration.Tests.Infrastructure;

namespace SqlHelper.Integration.Tests.Orchestration;

/// <summary>
/// Reported from production: every deployment through the tool added two spaces after the verb
/// ("ALTER PROCEDURE" became "ALTER   PROCEDURE", then five spaces, and so on). SQL Server stores
/// CREATE OR ALTER as CREATE plus the spaces that surrounded "OR ALTER". These tests run the real
/// engine against a real server, over and over, and check the stored text each time.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class VerbSpacingTests : IDisposable
{
    private readonly SqlServerFixture _sql;
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "sqlhelper-verb-tests", Guid.NewGuid().ToString("N"));
    private readonly AuditLog _audit;
    private readonly DeploymentEngine _engine;
    private static readonly ObjectName Name = ObjectName.Parse("dbo.usp_Spacing");

    public VerbSpacingTests(SqlServerFixture sql)
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

    private async Task<string> StoredAsync(DatabaseTarget target)
    {
        var inspector = new ObjectInspector();
        await using var connection = await _sql.Connections.OpenAsync(target, CancellationToken.None);
        ProgrammableObject? live = await inspector.GetAsync(connection, Name);
        Assert.NotNull(live);
        return live!.Definition;
    }

    [Fact]
    public async Task Deploying_the_same_procedure_ten_times_never_adds_spaces_after_the_verb()
    {
        DatabaseTarget client = await _sql.CreateClientDatabaseAsync("Spacing");
        await _sql.ExecOnAsync(client, "CREATE PROCEDURE dbo.usp_Spacing AS SELECT 1");

        for (int run = 0; run < 10; run++)
        {
            // Exactly what the operator does: script it out as ALTER and deploy it again.
            string scripted = "ALTER PROCEDURE dbo.usp_Spacing AS SELECT " + run;
            DeployResult result = await _engine.DeployObjectAsync([client], Name, scripted, BackupRoot, "again");
            Assert.Equal(1, result.SucceededCount);

            Assert.StartsWith("CREATE PROCEDURE dbo.usp_Spacing", await StoredAsync(client), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Patching_repeatedly_never_adds_spaces_after_the_verb()
    {
        DatabaseTarget client = await _sql.CreateClientDatabaseAsync("SpacingPatch");
        const string v0 = "CREATE PROCEDURE dbo.usp_Spacing AS\nBEGIN\n    SELECT 1\n    SELECT 2\nEND";
        await _sql.ExecOnAsync(client, v0);

        string current = v0;
        for (int run = 0; run < 5; run++)
        {
            string next = current.Replace("END", $"    SELECT {run + 10}\nEND", StringComparison.Ordinal);
            PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("p", current, next);
            Assert.True(built.Ok, built.Error);

            IReadOnlyList<PatchAttempt> attempts = await _engine.PlanPatchAsync([client], Name, built.Patch!);
            Assert.True(attempts[0].PatchedBody is not null, attempts[0].Summary);

            DeployResult deployed = await _engine.ApplyPatchAsync(attempts, Name, BackupRoot, "p");
            Assert.Equal(1, deployed.SucceededCount);

            current = await StoredAsync(client);
            Assert.StartsWith("CREATE PROCEDURE dbo.usp_Spacing", current, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_object_already_damaged_by_earlier_deployments_is_healed_on_the_next_one()
    {
        DatabaseTarget client = await _sql.CreateClientDatabaseAsync("Healed");

        // What two earlier CREATE OR ALTER deployments left on the server.
        await _sql.ExecOnAsync(client, "CREATE OR ALTER PROCEDURE dbo.usp_Spacing AS SELECT 1");
        await _sql.ExecOnAsync(client, "CREATE OR ALTER   PROCEDURE dbo.usp_Spacing AS SELECT 1");
        Assert.StartsWith("CREATE     PROCEDURE", await StoredAsync(client), StringComparison.Ordinal);

        DeployResult result = await _engine.DeployObjectAsync(
            [client], Name, "ALTER     PROCEDURE dbo.usp_Spacing AS SELECT 2", BackupRoot, "heal");
        Assert.Equal(1, result.SucceededCount);

        Assert.StartsWith("CREATE PROCEDURE dbo.usp_Spacing AS SELECT 2", await StoredAsync(client), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_brand_new_object_is_created_with_CREATE_and_stored_cleanly()
    {
        DatabaseTarget client = await _sql.CreateClientDatabaseAsync("New");

        DeployResult result = await _engine.DeployObjectAsync(
            [client], Name, "CREATE OR ALTER PROCEDURE dbo.usp_Spacing AS SELECT 1", BackupRoot, "new");
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal("new-object", result.Results[0].Method);

        Assert.StartsWith("CREATE PROCEDURE dbo.usp_Spacing", await StoredAsync(client), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_header_comments_body_and_line_endings_are_stored_byte_for_byte()
    {
        DatabaseTarget client = await _sql.CreateClientDatabaseAsync("Exact");
        await _sql.ExecOnAsync(client, "CREATE PROCEDURE dbo.usp_Spacing AS SELECT 0");

        const string script =
            "-- Author: A. Developer\r\n" +
            "--\tModified: tabs\tinside\r\n" +
            "ALTER PROCEDURE dbo.usp_Spacing\r\n" +
            "\t@id\t\tint\r\n" +
            "AS\r\n" +
            "BEGIN\r\n" +
            "    SELECT 'two  spaces', @id\r\n" +
            "END";

        DeployResult result = await _engine.DeployObjectAsync([client], Name, script, BackupRoot, "exact");
        Assert.Equal(1, result.SucceededCount);

        Assert.Equal(script.Replace("ALTER PROCEDURE", "CREATE PROCEDURE", StringComparison.Ordinal), await StoredAsync(client));
    }
}
