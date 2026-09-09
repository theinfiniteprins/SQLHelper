using SqlHelper.Core.Auditing;
using SqlHelper.Core.Model;
using SqlHelper.Core.Orchestration;
using SqlHelper.Core.Patching;
using SqlHelper.Core.Scripting;
using SqlHelper.Integration.Tests.Infrastructure;

namespace SqlHelper.Integration.Tests.Orchestration;

/// <summary>
/// The failure that reached production, end to end against a real server: a long procedure whose
/// body repeats verbatim, several scattered one-line additions, and a set of clients that are
/// mostly identical with one that has drifted.
///
/// Everything here goes through the real path — the definition is read back out of
/// <c>sys.sql_modules</c>, planned, applied inside a transaction, and read again to confirm what
/// actually landed.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RepeatedBlockPatchTests : IDisposable
{
    private readonly SqlServerFixture _sql;
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "sqlhelper-repeat-tests", Guid.NewGuid().ToString("N"));
    private readonly AuditLog _audit;
    private readonly DeploymentEngine _engine;

    public RepeatedBlockPatchTests(SqlServerFixture sql)
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

    private static readonly ObjectName Name = ObjectName.Parse("dbo.usp_LeaveApproval");

    /// <summary>
    /// A procedure in the shape that broke: a header comment, and a filter block that appears
    /// twice, verbatim, under two different joins.
    /// </summary>
    private const string Original = """
        -- =============================================
        -- Author:      A. Developer
        -- Description: Leave applications awaiting approval.
        -- =============================================
        CREATE PROCEDURE dbo.usp_LeaveApproval
                @StaffID        int,
                @StatusType     nvarchar(20)
        AS
        BEGIN
            SET NOCOUNT ON;

            SELECT
                    [LeaveApplicationID],
                    [StaffID],
                    [ApprovalLockLevel]
            INTO    #first
            FROM    dbo.HRL_LeaveApplicationApproval
            WHERE   [ApprovalStaffID] = @StaffID
            AND     (
                        @StatusType = 'All'
                        OR
                        (
                            [ApprovalStatus] = @StatusType
                            AND
                            (
                                [ApprovalStatus] <> 'Pending'
                                OR
                                (
                                    [ApprovalLevel] = ([ApprovalLockLevel] + 1)
                                    AND
                                    [HRApprovalStatus] IS NULL
                                )
                            )
                        )
                    )
            AND     [CancellationDateTime] IS NULL;

            SELECT
                    [LeaveApplicationID],
                    [StaffID],
                    [ApprovalLockLevel]
            FROM    dbo.HRL_LeaveApplicationApproval
            WHERE   [ApprovalStaffID] = @StaffID
            AND     (
                        @StatusType = 'All'
                        OR
                        (
                            [ApprovalStatus] = @StatusType
                            AND
                            (
                                [ApprovalStatus] <> 'Pending'
                                OR
                                (
                                    [ApprovalLevel] = ([ApprovalLockLevel] + 1)
                                    AND
                                    [HRApprovalStatus] IS NULL
                                )
                            )
                        )
                    )
            ORDER BY [LeaveApplicationID] DESC;

            DROP TABLE #first;
        END
        """;

    private static string Prerequisites => """
        CREATE TABLE dbo.HRL_LeaveApplicationApproval
        (
            LeaveApplicationID      int,
            StaffID                 int,
            ApprovalLockLevel       int,
            ApprovalStaffID         int,
            ApprovalStatus          nvarchar(20),
            ApprovalLevel           int,
            HRApprovalStatus        nvarchar(20),
            CancellationDateTime    datetime,
            IsDeleted               bit
        )
        """;

    /// <summary>The change: four scattered one-line additions, exactly the shape that failed.</summary>
    private static string WithFourAdditions(string script)
    {
        string result = script;
        result = InsertAfter(result, "        @StatusType     nvarchar(20)", "                -- reviewed");
        result = InsertAfter(result, "[StaffID],", "                [ApprovalStatus],");
        result = InsertAfter(result, "    AND     [CancellationDateTime] IS NULL;", "    -- only live rows");
        result = InsertAfter(result, "    ORDER BY [LeaveApplicationID] DESC;", "    -- newest first");
        return result;
    }

    private static string InsertAfter(string text, string marker, string newLine)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int at = Array.FindIndex(lines, l => l.Trim() == marker.Trim());
        Assert.True(at >= 0, $"marker not found: {marker}");
        var edited = new List<string>(lines);
        edited.Insert(at + 1, newLine);
        return string.Join('\n', edited);
    }

    private async Task<DatabaseTarget> ClientWithProcedureAsync(string name, string definition)
    {
        DatabaseTarget target = await _sql.CreateClientDatabaseAsync(name);
        await _sql.ExecOnAsync(target, Prerequisites);
        await _sql.ExecOnAsync(target, definition);
        return target;
    }

    [Fact]
    public async Task Four_scattered_additions_reach_every_identical_client_and_the_drifted_one()
    {
        // Five clients on the same version, plus one that has its own extra line.
        var identical = new List<DatabaseTarget>();
        for (int i = 1; i <= 5; i++)
        {
            identical.Add(await ClientWithProcedureAsync($"Same-{i}", Original));
        }

        string driftedSource = InsertAfter(Original, "        SET NOCOUNT ON;", "    -- this client logs differently");
        DatabaseTarget drifted = await ClientWithProcedureAsync("Drifted", driftedSource);

        List<DatabaseTarget> all = [.. identical, drifted];

        // The operator authors the change once, against one client's version.
        string after = WithFourAdditions(Original);
        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-99", Original, after);
        Assert.True(built.Ok, built.Error);
        Assert.Equal(4, built.Patch!.Hunks.Count);

        IReadOnlyList<PatchAttempt> attempts = await _engine.PlanPatchAsync(all, Name, built.Patch!);

        // Every client must be placeable - none may come back needing a hand edit.
        var stuck = attempts
            .Where(a => a.Outcome is PatchOutcome.ManualRequired or PatchOutcome.ValidationFailed)
            .Select(a => $"{a.Target.ClientName}: {a.Summary}")
            .ToList();
        Assert.Empty(stuck);

        DeployResult deployed = await _engine.ApplyPatchAsync(attempts, Name, BackupRoot, "ORD-99 rollout", "ORD-99");
        Assert.Equal(all.Count, deployed.SucceededCount);
        Assert.Equal(0, deployed.FailedCount);

        var inspector = new ObjectInspector();
        foreach (DatabaseTarget target in all)
        {
            await using var connection = await _sql.Connections.OpenAsync(target, CancellationToken.None);
            ProgrammableObject? live = await inspector.GetAsync(connection, Name);
            Assert.NotNull(live);

            // All four additions landed.
            Assert.Contains("-- reviewed", live!.Definition, StringComparison.Ordinal);
            Assert.Contains("[ApprovalStatus],", live.Definition, StringComparison.Ordinal);
            Assert.Contains("-- only live rows", live.Definition, StringComparison.Ordinal);
            Assert.Contains("-- newest first", live.Definition, StringComparison.Ordinal);

            // The header comment survived the deployment.
            Assert.Contains("-- Author:      A. Developer", live.Definition, StringComparison.Ordinal);

            // The addition below the first filter block went in once, not twice.
            Assert.Equal(1, Count(live.Definition, "-- only live rows"));
        }

        // The drifted client kept its own line.
        await using var driftedConnection = await _sql.Connections.OpenAsync(drifted, CancellationToken.None);
        ProgrammableObject? driftedNow = await inspector.GetAsync(driftedConnection, Name);
        Assert.Contains("this client logs differently", driftedNow!.Definition, StringComparison.Ordinal);

        // And every client's own prior version was backed up before it was touched.
        foreach (DatabaseTarget target in all)
        {
            string[] backups = Directory.GetFiles(
                Path.Combine(deployed.BackupFolder, target.ClientName), "dbo.usp_LeaveApproval_*.sql");
            Assert.Single(backups);
            string content = await File.ReadAllTextAsync(backups[0]);
            Assert.DoesNotContain("-- only live rows", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_change_in_the_first_of_two_identical_blocks_does_not_touch_the_second()
    {
        DatabaseTarget client = await ClientWithProcedureAsync("Solo", Original);

        // Add a line inside the first copy of the duplicated filter only.
        string[] lines = Original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int firstBlock = Array.FindIndex(lines, l => l.Trim() == "@StatusType = 'All'");
        Assert.True(firstBlock > 0);
        Assert.Contains(lines.Skip(firstBlock + 1), l => l.Trim() == "@StatusType = 'All'"); // there really are two

        var edited = new List<string>(lines);
        edited.Insert(firstBlock + 1, "                        -- first block only");
        string after = string.Join('\n', edited);

        PatchDefinitionResult built = PatchDefinitionFactory.FromReferenceDiff("ORD-100", Original, after);
        Assert.True(built.Ok, built.Error);

        IReadOnlyList<PatchAttempt> attempts = await _engine.PlanPatchAsync([client], Name, built.Patch!);
        Assert.True(attempts[0].PatchedBody is not null, $"{attempts[0].Outcome}: {attempts[0].Summary}");

        DeployResult deployed = await _engine.ApplyPatchAsync(attempts, Name, BackupRoot, "ORD-100", "ORD-100");
        Assert.Equal(1, deployed.SucceededCount);

        var inspector = new ObjectInspector();
        await using var connection = await _sql.Connections.OpenAsync(client, CancellationToken.None);
        ProgrammableObject? live = await inspector.GetAsync(connection, Name);

        Assert.Equal(1, Count(live!.Definition, "-- first block only"));

        // Both filter blocks are still there - the change did not overwrite the second one.
        Assert.Equal(2, Count(live.Definition, "@StatusType = 'All'"));
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
