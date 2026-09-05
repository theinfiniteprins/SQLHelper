using System.Data;
using SqlHelper.Core.Execution;

namespace SqlHelper.App.ViewModels;

/// <summary>Turns a <see cref="QueryTable"/> into a <see cref="DataTable"/> so a plain WPF DataGrid can auto-generate columns for it.</summary>
public static class QueryResultPresentation
{
    public static DataTable ToDataTable(QueryTable table)
    {
        var dt = new DataTable();
        foreach (QueryColumn column in table.Columns)
        {
            dt.Columns.Add(MakeUniqueName(dt, column.Name), typeof(object));
        }

        foreach (object?[] row in table.Rows)
        {
            DataRow dr = dt.NewRow();
            for (int i = 0; i < row.Length; i++)
            {
                dr[i] = row[i] ?? DBNull.Value;
            }

            dt.Rows.Add(dr);
        }

        return dt;
    }

    private static string MakeUniqueName(DataTable table, string name)
    {
        string candidate = string.IsNullOrWhiteSpace(name) ? "(no name)" : name;
        int suffix = 2;
        while (table.Columns.Contains(candidate))
        {
            candidate = $"{name}_{suffix++}";
        }

        return candidate;
    }
}

/// <summary>One client's results (or failure) on the Select screen.</summary>
public sealed class ClientResultTab
{
    public required string ClientName { get; init; }

    public required bool Success { get; init; }

    public string? Error { get; init; }

    public DataView? PrimaryTable { get; init; }

    public int RowCount { get; init; }

    public bool Truncated { get; init; }

    public IReadOnlyList<string> Messages { get; init; } = [];

    /// <summary>Short row-count or failure marker shown on the tab.</summary>
    public string Badge => Success ? $"{RowCount:N0}{(Truncated ? "+" : "")}" : "failed";

    public string Header => Success ? $"{ClientName} ({Badge})" : $"{ClientName} — failed";
}
