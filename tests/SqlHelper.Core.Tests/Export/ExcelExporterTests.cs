using ClosedXML.Excel;
using SqlHelper.Core.Execution;
using SqlHelper.Core.Export;

namespace SqlHelper.Core.Tests.Export;

public sealed class ExcelExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlhelper-xlsx-tests", Guid.NewGuid().ToString("N"));

    public ExcelExporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static QueryTable Table() => new()
    {
        Columns = [new QueryColumn("Id", "Int32", "int"), new QueryColumn("Name", "String", "nvarchar")],
        Rows = [[1, "Acme Order"], [2, null]],
    };

    [Fact]
    public void ExportPerClientSheets_creates_one_sheet_per_client()
    {
        string path = Path.Combine(_dir, "out.xlsx");
        ExcelExporter.ExportPerClientSheets(path, [("Acme", Table()), ("Globex", Table())]);

        using var workbook = new XLWorkbook(path);
        Assert.Equal(["Acme", "Globex"], workbook.Worksheets.Select(s => s.Name));
        Assert.Equal("Id", workbook.Worksheet("Acme").Cell(1, 1).GetString());
        Assert.Equal("Acme Order", workbook.Worksheet("Acme").Cell(2, 2).GetString());
    }

    [Fact]
    public void ExportCombined_prefixes_every_row_with_the_client_name()
    {
        string path = Path.Combine(_dir, "combined.xlsx");
        ExcelExporter.ExportCombined(path, [("Acme", Table()), ("Globex", Table())]);

        using var workbook = new XLWorkbook(path);
        IXLWorksheet sheet = workbook.Worksheet("Results");

        Assert.Equal("Client", sheet.Cell(1, 1).GetString());
        Assert.Equal("Acme", sheet.Cell(2, 1).GetString());
        Assert.Equal("Globex", sheet.Cell(4, 1).GetString());
        Assert.Equal(5, sheet.LastRowUsed()!.RowNumber()); // header + 2 rows per client x 2 clients
    }

    [Fact]
    public void Duplicate_client_names_get_unique_sheet_names()
    {
        string path = Path.Combine(_dir, "dupes.xlsx");
        ExcelExporter.ExportPerClientSheets(path, [("Acme", Table()), ("Acme", Table())]);

        using var workbook = new XLWorkbook(path);
        Assert.Equal(2, workbook.Worksheets.Count);
        Assert.Equal(2, workbook.Worksheets.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public void Null_values_export_as_blank_cells()
    {
        string path = Path.Combine(_dir, "nulls.xlsx");
        ExcelExporter.ExportPerClientSheets(path, [("Acme", Table())]);

        using var workbook = new XLWorkbook(path);
        Assert.True(workbook.Worksheet("Acme").Cell(3, 2).IsEmpty());
    }
}
