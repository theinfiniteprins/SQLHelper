using ClosedXML.Excel;
using SqlHelper.Core.Execution;

namespace SqlHelper.Core.Export;

/// <summary>Exports client-wise query results to a workbook, without needing Excel installed.</summary>
public static class ExcelExporter
{
    private static readonly char[] InvalidSheetChars = ['\\', '/', '?', '*', '[', ']', ':'];

    /// <summary>One worksheet per client, each named after the client (truncated/sanitised to fit Excel's rules).</summary>
    public static void ExportPerClientSheets(string filePath, IReadOnlyList<(string Client, QueryTable Table)> results)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            throw new InvalidOperationException("Nothing to export.");
        }

        using var workbook = new XLWorkbook();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string client, QueryTable table) in results)
        {
            string name = UniqueSheetName(client, usedNames);
            IXLWorksheet sheet = workbook.Worksheets.Add(name);
            WriteTable(sheet, table, startColumn: 1);
        }

        workbook.SaveAs(filePath);
    }

    /// <summary>One worksheet with every client's rows stacked, a leading "Client" column identifying each.</summary>
    public static void ExportCombined(string filePath, IReadOnlyList<(string Client, QueryTable Table)> results)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            throw new InvalidOperationException("Nothing to export.");
        }

        using var workbook = new XLWorkbook();
        IXLWorksheet sheet = workbook.Worksheets.Add("Results");

        sheet.Cell(1, 1).Value = "Client";
        IReadOnlyList<QueryColumn> columns = results[0].Table.Columns;
        for (int c = 0; c < columns.Count; c++)
        {
            sheet.Cell(1, c + 2).Value = columns[c].Name;
        }

        sheet.Row(1).Style.Font.Bold = true;

        int row = 2;
        foreach ((string client, QueryTable table) in results)
        {
            foreach (object?[] values in table.Rows)
            {
                sheet.Cell(row, 1).Value = client;
                for (int c = 0; c < values.Length; c++)
                {
                    SetCell(sheet.Cell(row, c + 2), values[c]);
                }

                row++;
            }
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, 200);
        workbook.SaveAs(filePath);
    }

    private static void WriteTable(IXLWorksheet sheet, QueryTable table, int startColumn)
    {
        for (int c = 0; c < table.Columns.Count; c++)
        {
            sheet.Cell(1, startColumn + c).Value = table.Columns[c].Name;
        }

        sheet.Row(1).Style.Font.Bold = true;

        for (int r = 0; r < table.Rows.Count; r++)
        {
            object?[] values = table.Rows[r];
            for (int c = 0; c < values.Length; c++)
            {
                SetCell(sheet.Cell(r + 2, startColumn + c), values[c]);
            }
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, 200);

        if (table.Truncated)
        {
            sheet.Cell(table.Rows.Count + 3, startColumn).Value = "Row limit reached — results were truncated.";
        }
    }

    private static void SetCell(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                cell.Value = XLCellValue.FromObject(null);
                break;
            case DateTime dt:
                cell.Value = dt;
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                break;
            case DateTimeOffset dto:
                cell.Value = dto.LocalDateTime;
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                break;
            case bool b:
                cell.Value = b;
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                cell.Value = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                break;
            case byte[] bytes:
                cell.Value = Convert.ToBase64String(bytes);
                break;
            default:
                cell.Value = value.ToString();
                break;
        }
    }

    private static string UniqueSheetName(string client, HashSet<string> used)
    {
        string sanitized = new(client.Select(c => InvalidSheetChars.Contains(c) ? '_' : c).ToArray());
        sanitized = sanitized.Trim();
        if (sanitized.Length == 0)
        {
            sanitized = "Client";
        }

        if (sanitized.Length > 31)
        {
            sanitized = sanitized[..31];
        }

        string candidate = sanitized;
        int suffix = 2;
        while (!used.Add(candidate))
        {
            string suffixText = $"_{suffix++}";
            candidate = sanitized[..Math.Min(sanitized.Length, 31 - suffixText.Length)] + suffixText;
        }

        return candidate;
    }
}
