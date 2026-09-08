using System.Data;
using SqlHelper.App.ViewModels;
using SqlHelper.Core.Execution;

namespace SqlHelper.App.Tests;

/// <summary>
/// An unaliased SELECT COUNT(*) produced a column with no name. Bound by name as a property path
/// it rendered blank, so the result looked empty until the operator added an alias. Columns now
/// carry safe internal names, with the server's real name shown in the header.
/// </summary>
public sealed class QueryResultPresentationTests
{
    private static QueryTable Table(params string[] columnNames) => new()
    {
        Columns = [.. columnNames.Select(n => new QueryColumn(n, "object", "object"))],
        Rows = [[.. columnNames.Select((_, i) => (object?)(i + 1))]],
    };

    [Fact]
    public void Columns_are_named_so_the_grid_can_always_bind_to_them()
    {
        DataTable dt = QueryResultPresentation.ToDataTable(Table("(No column name)"));

        Assert.Equal("c0", dt.Columns[0].ColumnName);
        Assert.Equal("(No column name)", dt.Columns[0].Caption);
    }

    [Fact]
    public void The_value_of_an_unnamed_column_actually_reaches_the_row()
    {
        DataTable dt = QueryResultPresentation.ToDataTable(Table("(No column name)"));

        Assert.Single(dt.Rows);
        Assert.Equal(1, dt.Rows[0]["c0"]);
    }

    [Theory]
    [InlineData("Total.Amount")]
    [InlineData("Orders/Day")]
    [InlineData("Rate (%)")]
    [InlineData("Col]umn")]
    [InlineData("StudentCount")]
    public void Any_name_the_server_gives_is_shown_as_the_header_and_still_binds(string name)
    {
        DataTable dt = QueryResultPresentation.ToDataTable(Table(name));

        Assert.Equal("c0", dt.Columns[0].ColumnName);
        Assert.Equal(name, QueryResultPresentation.HeaderFor(dt, "c0"));
        Assert.Equal(1, dt.Rows[0]["c0"]);
    }

    [Fact]
    public void Two_columns_with_the_same_name_no_longer_collide()
    {
        // SELECT COUNT(*), COUNT(*) — two unnamed columns, which used to need uniquifying.
        DataTable dt = QueryResultPresentation.ToDataTable(Table("(No column name)", "(No column name)"));

        Assert.Equal(2, dt.Columns.Count);
        Assert.Equal("(No column name)", QueryResultPresentation.HeaderFor(dt, "c0"));
        Assert.Equal("(No column name)", QueryResultPresentation.HeaderFor(dt, "c1"));
        Assert.Equal(1, dt.Rows[0]["c0"]);
        Assert.Equal(2, dt.Rows[0]["c1"]);
    }

    [Fact]
    public void A_blank_name_from_the_driver_is_still_labelled_for_the_reader()
    {
        Assert.Equal("(No column name)", QueryResultPresentation.DisplayName("", 0));
        Assert.Equal("(No column name)", QueryResultPresentation.DisplayName(null, 3));
        Assert.Equal("Name", QueryResultPresentation.DisplayName("Name", 0));
    }

    [Fact]
    public void Nulls_survive_the_trip_into_the_grid()
    {
        var table = new QueryTable
        {
            Columns = [new QueryColumn("Name", "object", "object")],
            Rows = [[null]],
        };

        DataTable dt = QueryResultPresentation.ToDataTable(table);

        Assert.Equal(DBNull.Value, dt.Rows[0]["c0"]);
    }
}

/// <summary>The Excel export must show the server's column names, not the grid's internal ones.</summary>
public sealed class QueryResultExportTests
{
    private static QueryTable Source() => new()
    {
        Columns =
        [
            new QueryColumn("(No column name)", "object", "object"),
            new QueryColumn("StudentName", "object", "object"),
        ],
        Rows = [[115, "Asha"], [7, null]],
    };

    [Fact]
    public void The_round_trip_to_the_export_keeps_the_real_column_names()
    {
        QueryTable exported = QueryResultPresentation.ToQueryTable(
            QueryResultPresentation.ToDataTable(Source()).DefaultView);

        Assert.Equal(["(No column name)", "StudentName"], exported.Columns.Select(c => c.Name));
        Assert.DoesNotContain(exported.Columns, c => c.Name.StartsWith('c') && c.Name.Length == 2);
    }

    [Fact]
    public void The_round_trip_keeps_every_value_including_nulls()
    {
        QueryTable exported = QueryResultPresentation.ToQueryTable(
            QueryResultPresentation.ToDataTable(Source()).DefaultView);

        Assert.Equal(2, exported.Rows.Count);
        Assert.Equal(115, exported.Rows[0][0]);
        Assert.Equal("Asha", exported.Rows[0][1]);
        Assert.Equal(7, exported.Rows[1][0]);
        Assert.Null(exported.Rows[1][1]);
    }
}

/// <summary>
/// Column headers are rendered through AccessText, which treats a single underscore as an
/// access-key marker and hides it — so a column called type_desc appeared as typedesc.
/// </summary>
public sealed class QueryResultHeaderTests
{
    private static System.Data.DataTable OneColumn(string name) =>
        QueryResultPresentation.ToDataTable(new QueryTable
        {
            Columns = [new QueryColumn(name, "object", "object")],
            Rows = [[1]],
        });

    [Theory]
    [InlineData("type_desc", "type__desc")]
    [InlineData("Student_Name", "Student__Name")]
    [InlineData("_leading", "__leading")]
    [InlineData("a_b_c", "a__b__c")]
    public void An_underscore_in_a_column_name_is_still_shown_to_the_reader(string name, string expected)
    {
        Assert.Equal(expected, QueryResultPresentation.HeaderTextFor(OneColumn(name), "c0"));

        // The underlying name is untouched — only the rendered header is escaped.
        Assert.Equal(name, QueryResultPresentation.HeaderFor(OneColumn(name), "c0"));
    }

    [Fact]
    public void A_name_with_no_underscore_is_left_alone()
    {
        Assert.Equal("StudentName", QueryResultPresentation.HeaderTextFor(OneColumn("StudentName"), "c0"));
        Assert.Equal("(No column name)", QueryResultPresentation.HeaderTextFor(OneColumn(""), "c0"));
    }
}
