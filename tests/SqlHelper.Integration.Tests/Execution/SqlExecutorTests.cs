using SqlHelper.Core.Execution;
using SqlHelper.Integration.Tests.Infrastructure;

namespace SqlHelper.Integration.Tests.Execution;

[Collection(SqlServerCollection.Name)]
public sealed class SqlExecutorTests
{
    private readonly SqlServerFixture _sql;

    public SqlExecutorTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task Query_returns_columns_and_rows()
    {
        QueryResult result = await _sql.Executor.ExecuteQueryAsync(
            _sql.Target,
            "SELECT 1 AS Id, CAST('Acme' AS nvarchar(20)) AS Name UNION ALL SELECT 2, 'Globex' ORDER BY Id");

        QueryTable table = Assert.Single(result.Tables);
        Assert.Equal(["Id", "Name"], table.Columns.Select(c => c.Name));
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(2, table.Rows[1][0]);
        Assert.Equal("Globex", table.Rows[1][1]);
    }

    [Fact]
    public async Task Query_captures_print_messages_and_nulls()
    {
        QueryResult result = await _sql.Executor.ExecuteQueryAsync(
            _sql.Target,
            "PRINT 'hello from the server';\nSELECT CAST(NULL AS int) AS MaybeNull;");

        Assert.Contains("hello from the server", result.Messages);
        Assert.Null(result.Tables[0].Rows[0][0]);
    }

    [Fact]
    public async Task Query_returns_multiple_result_sets()
    {
        QueryResult result = await _sql.Executor.ExecuteQueryAsync(
            _sql.Target,
            "SELECT 1 AS A; SELECT 'x' AS B, 'y' AS C;");

        Assert.Equal(2, result.Tables.Count);
        Assert.Equal(["B", "C"], result.Tables[1].Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Query_row_cap_marks_the_table_truncated()
    {
        QueryResult result = await _sql.Executor.ExecuteQueryAsync(
            _sql.Target,
            "SELECT TOP (50) object_id FROM sys.all_objects",
            new QueryExecutionOptions { MaxRows = 10 });

        Assert.True(result.Tables[0].Truncated);
        Assert.Equal(10, result.Tables[0].Rows.Count);
    }

    [Fact]
    public async Task Query_wraps_in_a_transaction_that_is_rolled_back()
    {
        await _sql.ExecAsync("CREATE TABLE dbo.RollbackProbe (Id int);");

        // A sneaky INSERT would normally be blocked by the read-only guard; here we prove the
        // executor's own safety net rolls it back even if something slips through.
        await _sql.Executor.ExecuteQueryAsync(
            _sql.Target,
            "INSERT INTO dbo.RollbackProbe (Id) VALUES (99); SELECT * FROM dbo.RollbackProbe;");

        QueryResult after = await _sql.Executor.ExecuteQueryAsync(
            _sql.Target, "SELECT COUNT(*) FROM dbo.RollbackProbe");

        Assert.Equal(0, after.Tables[0].Rows[0][0]);
    }

    [Fact]
    public async Task NonQuery_reports_rows_affected_per_batch()
    {
        await _sql.ExecAsync("CREATE TABLE dbo.Widgets (Id int IDENTITY, Status varchar(10));");
        await _sql.ExecAsync("INSERT INTO dbo.Widgets (Status) VALUES ('old'),('old'),('old'),('keep');");

        NonQueryResult result = await _sql.Executor.ExecuteNonQueryAsync(
            _sql.Target,
            "UPDATE dbo.Widgets SET Status = 'new' WHERE Status = 'old';\nGO\nDELETE FROM dbo.Widgets WHERE Status = 'keep';",
            NonQueryExecutionOptions.Default);

        Assert.False(result.WasRolledBack);
        Assert.Equal(3, result.Batches[0].RowsAffected);
        Assert.Equal(1, result.Batches[1].RowsAffected);
    }

    [Fact]
    public async Task NonQuery_dry_run_reports_counts_but_changes_nothing()
    {
        await _sql.ExecAsync("CREATE TABLE dbo.DryRunProbe (Id int);");
        await _sql.ExecAsync("INSERT INTO dbo.DryRunProbe VALUES (1),(2),(3);");

        NonQueryResult dry = await _sql.Executor.ExecuteNonQueryAsync(
            _sql.Target,
            "DELETE FROM dbo.DryRunProbe",
            NonQueryExecutionOptions.Default.AsDryRun());

        Assert.True(dry.WasRolledBack);
        Assert.Equal(3, dry.Batches[0].RowsAffected);

        QueryResult after = await _sql.Executor.ExecuteQueryAsync(_sql.Target, "SELECT COUNT(*) FROM dbo.DryRunProbe");
        Assert.Equal(3, after.Tables[0].Rows[0][0]);
    }

    [Fact]
    public async Task NonQuery_whole_script_transaction_rolls_back_on_a_later_failure()
    {
        await _sql.ExecAsync("CREATE TABLE dbo.TxProbe (Id int PRIMARY KEY);");

        NonQueryResult result = await _sql.Executor.ExecuteNonQueryAsync(
            _sql.Target,
            "INSERT INTO dbo.TxProbe VALUES (1);\nGO\nINSERT INTO dbo.TxProbe VALUES (1);", // PK violation
            NonQueryExecutionOptions.Default with { StopOnError = true });

        Assert.True(result.WasRolledBack);

        QueryResult after = await _sql.Executor.ExecuteQueryAsync(_sql.Target, "SELECT COUNT(*) FROM dbo.TxProbe");
        Assert.Equal(0, after.Tables[0].Rows[0][0]);
    }

    [Fact]
    public async Task TestConnection_reports_server_and_database()
    {
        ConnectionProbe probe = await _sql.Executor.TestConnectionAsync(_sql.Target);

        Assert.True(probe.Ok, probe.Error);
        Assert.Equal(_sql.DatabaseName, probe.DatabaseName);
        Assert.False(string.IsNullOrWhiteSpace(probe.ServerVersion));
    }
}
