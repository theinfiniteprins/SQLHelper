using SqlHelper.Core.Guard;

namespace SqlHelper.Core.Tests.Guard;

public sealed class ReadOnlyGuardTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT * FROM dbo.Orders WHERE Total > 100 ORDER BY CreatedOn DESC")]
    [InlineData("SELECT o.Id, c.Name FROM dbo.Orders o JOIN dbo.Customers c ON c.Id = o.CustomerId")]
    [InlineData("WITH recent AS (SELECT * FROM dbo.Orders WHERE CreatedOn > '2026-01-01') SELECT * FROM recent")]
    [InlineData("SELECT Id FROM dbo.A UNION SELECT Id FROM dbo.B")]
    [InlineData("SELECT @c = COUNT(*) FROM dbo.Orders")]
    [InlineData("SET NOCOUNT ON;\nSELECT TOP (10) * FROM dbo.Orders")]
    [InlineData("DECLARE @since date = '2026-01-01';\nSELECT * FROM dbo.Orders WHERE CreatedOn >= @since")]
    [InlineData("IF EXISTS (SELECT 1 FROM dbo.Orders) SELECT 'yes' ELSE SELECT 'no'")]
    [InlineData("DECLARE @t TABLE (Id int); SELECT 1")]
    [InlineData("SELECT 1 -- TODO: UPDATE this later\n")]
    public void Accepts_read_only_scripts(string sql)
    {
        ReadOnlyVerdict verdict = ReadOnlyGuard.Inspect(sql);

        Assert.True(verdict.IsReadOnly, verdict.Summary);
        Assert.Empty(verdict.Violations);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.Orders (Total) VALUES (1)", "INSERT")]
    [InlineData("UPDATE dbo.Orders SET Total = 0 WHERE Id = 5", "UPDATE")]
    [InlineData("DELETE FROM dbo.Orders WHERE Id = 5", "DELETE")]
    [InlineData("MERGE dbo.Target t USING dbo.Src s ON s.Id = t.Id WHEN NOT MATCHED THEN INSERT (Id) VALUES (s.Id);", "MERGE")]
    [InlineData("TRUNCATE TABLE dbo.Orders", "TRUNCATE TABLE")]
    [InlineData("DROP TABLE dbo.Orders", "DropTable")]
    [InlineData("EXEC sp_who", "EXEC / EXECUTE")]
    [InlineData("EXEC ('SELECT 1')", "EXEC / EXECUTE")]
    [InlineData("EXEC sp_executesql N'DELETE FROM dbo.Orders'", "EXEC / EXECUTE")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.usp_X AS SELECT 1", "CreateOrAlterProcedure")]
    [InlineData("ALTER TABLE dbo.Orders ADD Extra int NULL", "AlterTable")]
    [InlineData("GRANT SELECT ON dbo.Orders TO public", "Grant")]
    public void Rejects_writes_and_ddl(string sql, string expectedConstructFragment)
    {
        ReadOnlyVerdict verdict = ReadOnlyGuard.Inspect(sql);

        Assert.False(verdict.IsReadOnly);
        Assert.Contains(verdict.Violations, v => v.Construct.Contains(expectedConstructFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_select_into()
    {
        ReadOnlyVerdict verdict = ReadOnlyGuard.Inspect("SELECT Id, Total INTO #snapshot FROM dbo.Orders");

        Assert.False(verdict.IsReadOnly);
        Assert.Contains(verdict.Violations, v => v.Construct.Contains("INTO", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_a_write_hidden_after_a_valid_select_in_the_same_batch()
    {
        ReadOnlyVerdict verdict = ReadOnlyGuard.Inspect("SELECT * FROM dbo.Orders;\nDELETE FROM dbo.Orders WHERE Id = 1;");

        Assert.False(verdict.IsReadOnly);
        Assert.Contains(verdict.Violations, v => v.Construct == "DELETE");
    }

    [Fact]
    public void Rejects_a_cte_that_feeds_a_delete()
    {
        ReadOnlyVerdict verdict = ReadOnlyGuard.Inspect(
            "WITH doomed AS (SELECT * FROM dbo.Orders WHERE Total = 0) DELETE FROM doomed");

        Assert.False(verdict.IsReadOnly);
    }

    [Fact]
    public void Unparseable_script_is_not_trusted_and_reports_parse_errors()
    {
        ReadOnlyVerdict verdict = ReadOnlyGuard.Inspect("SELECT FROM WHERE ORDER GROUP");

        Assert.False(verdict.IsReadOnly);
        Assert.NotEmpty(verdict.ParseErrors);
        Assert.False(verdict.ParsedCleanly);
    }

    [Fact]
    public void Empty_script_is_rejected()
    {
        Assert.False(ReadOnlyGuard.Inspect("   ").IsReadOnly);
    }

    [Fact]
    public void Violations_are_ordered_by_position()
    {
        ReadOnlyVerdict verdict = ReadOnlyGuard.Inspect(
            "SELECT 1;\nUPDATE dbo.A SET x = 1;\nSELECT 2;\nDELETE FROM dbo.B;");

        Assert.Collection(
            verdict.Violations,
            first => Assert.Equal("UPDATE", first.Construct),
            second => Assert.Equal("DELETE", second.Construct));
        Assert.True(verdict.Violations[0].Line < verdict.Violations[1].Line);
    }
}
