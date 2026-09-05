using SqlHelper.Core.Model;
using SqlHelper.Core.Registry;

namespace SqlHelper.Core.Tests.Registry;

public sealed class TargetImporterTests
{
    [Fact]
    public void Parses_a_pasted_excel_block_with_a_header()
    {
        string pasted =
            "Client\tServer\tDatabase\tAuth\tUser\tEnvironment\n" +
            "Acme\tsql-01\tAcmeDb\tSQL\tapp_user\tProduction\n" +
            "Globex\tsql-02\tGlobexDb\tWindows\t\tDev\n";

        IReadOnlyList<ImportedTargetRow> rows = TargetImporter.ParseTabSeparated(pasted);

        Assert.Equal(2, rows.Count);
        Assert.Equal(AuthMode.SqlLogin, rows[0].AuthMode);
        Assert.Equal("app_user", rows[0].UserId);
        Assert.Equal(ServerEnvironment.Production, rows[0].Environment);
        Assert.Equal(AuthMode.WindowsIntegrated, rows[1].AuthMode);
        Assert.Equal(ServerEnvironment.Development, rows[1].Environment);
    }

    [Fact]
    public void Works_without_a_header_row()
    {
        IReadOnlyList<ImportedTargetRow> rows = TargetImporter.ParseTabSeparated("Acme\tsql-01\tAcmeDb");

        Assert.Single(rows);
        Assert.Equal("Acme", rows[0].ClientName);
    }

    [Fact]
    public void Missing_trailing_columns_default_sensibly()
    {
        ImportedTargetRow row = TargetImporter.ParseTabSeparated("Acme\tsql-01\tAcmeDb").Single();

        Assert.Equal(AuthMode.WindowsIntegrated, row.AuthMode);
        Assert.Null(row.UserId);
        Assert.Equal(ServerEnvironment.Production, row.Environment);
    }

    [Fact]
    public void Blank_required_fields_produce_warnings_but_still_parse()
    {
        ImportedTargetRow row = TargetImporter.ParseTabSeparated("\tsql-01\t").Single();

        Assert.Contains(row.Warnings, w => w.Contains("Client", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(row.Warnings, w => w.Contains("Database", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sql_login_without_a_user_id_warns()
    {
        ImportedTargetRow row = TargetImporter.ParseTabSeparated("Acme\tsql-01\tAcmeDb\tSQL").Single();

        Assert.Contains(row.Warnings, w => w.Contains("user id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Blank_lines_are_skipped()
    {
        IReadOnlyList<ImportedTargetRow> rows = TargetImporter.ParseTabSeparated("Acme\tsql-01\tAcmeDb\n\n\nGlobex\tsql-02\tGlobexDb\n");

        Assert.Equal(2, rows.Count);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("Y", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("x", true)]
    [InlineData("no", false)]
    [InlineData("", false)]
    public void Trust_certificate_column_accepts_the_usual_spreadsheet_spellings(string cell, bool expected)
    {
        // Saves ticking the box by hand on every one of thirty self-signed internal servers.
        ImportedTargetRow row = TargetImporter
            .ParseTabSeparated($"Acme\tsql-01\tAcmeDb\tWindows\t\tProduction\t{cell}").Single();

        Assert.Equal(expected, row.TrustServerCertificate);
        Assert.Equal(expected, row.ToTarget().TrustServerCertificate);
    }

    [Fact]
    public void ToTarget_maps_every_field()
    {
        ImportedTargetRow row = TargetImporter.ParseTabSeparated("Acme\tsql-01\tAcmeDb\tSQL\tapp_user\tStaging").Single();
        var target = row.ToTarget();

        Assert.Equal("Acme", target.ClientName);
        Assert.Equal("sql-01", target.ServerName);
        Assert.Equal("AcmeDb", target.DatabaseName);
        Assert.Equal(AuthMode.SqlLogin, target.AuthMode);
        Assert.Equal("app_user", target.UserId);
        Assert.Equal(ServerEnvironment.Staging, target.Environment);
    }
}
