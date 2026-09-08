using System.Data;
using SqlHelper.Core.Execution;

namespace SqlHelper.App.ViewModels;

/// <summary>Turns a <see cref="QueryTable"/> into a <see cref="DataTable"/> so a plain WPF DataGrid can auto-generate columns for it.</summary>
public static class QueryResultPresentation
{
    /// <summary>
    /// Internal name given to the nth result column.
    ///
    /// The DataGrid generates each column bound to the column's name <em>as a property path</em>,
    /// and a path is not free text — <c>(</c> opens an attached property, <c>.</c> walks into a
    /// sub-property, <c>[</c> opens an indexer. An unaliased <c>SELECT COUNT(*)</c> has no name at
    /// all, so it ends up called <c>(No column name)</c>, the path fails to parse, and the column
    /// renders empty. That is exactly why adding an alias made the value appear.
    ///
    /// So the grid never sees the real name: columns are named <c>c0</c>, <c>c1</c>, … which always
    /// parse, and the real name rides along in <see cref="DataColumn.Caption"/> for the header.
    /// </summary>
    public static string InternalName(int index) => $"c{index}";

    public static DataTable ToDataTable(QueryTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var dt = new DataTable();
        for (int i = 0; i < table.Columns.Count; i++)
        {
            dt.Columns.Add(new DataColumn(InternalName(i), typeof(object))
            {
                Caption = DisplayName(table.Columns[i].Name, i),
            });
        }

        foreach (object?[] row in table.Rows)
        {
            DataRow dr = dt.NewRow();
            for (int i = 0; i < row.Length && i < dt.Columns.Count; i++)
            {
                dr[i] = row[i] ?? DBNull.Value;
            }

            dt.Rows.Add(dr);
        }

        return dt;
    }

    /// <summary>What the operator should see above the column.</summary>
    public static string DisplayName(string? name, int index) =>
        string.IsNullOrWhiteSpace(name) ? "(No column name)" : name;

    /// <summary>
    /// Back the other way, for the Excel export: the grid's rows carry safe internal names, but the
    /// exported sheet must show the names the server actually returned.
    /// </summary>
    public static QueryTable ToQueryTable(DataView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        DataTable table = view.Table!;
        var columns = table.Columns.Cast<DataColumn>()
            .Select(c => new QueryColumn(
                string.IsNullOrEmpty(c.Caption) ? c.ColumnName : c.Caption, "object", "object"))
            .ToList();

        var rows = table.Rows.Cast<DataRow>()
            .Select(r => columns.Select((_, i) => r[i] is DBNull ? null : r[i]).ToArray())
            .ToList();

        return new QueryTable { Columns = columns, Rows = rows };
    }

    /// <summary>
    /// The header text to actually put on the column.
    ///
    /// A column header is rendered through an <c>AccessText</c>, which reads a single underscore as
    /// "the next character is the access key" and swallows it — so <c>type_desc</c> came out as
    /// <c>typedesc</c>. Doubling underscores renders one.
    /// </summary>
    public static string HeaderTextFor(DataTable? table, string internalName) =>
        HeaderFor(table, internalName).Replace("_", "__", StringComparison.Ordinal);

    /// <summary>The header for a generated column, given the internal name the grid reports.</summary>
    public static string HeaderFor(DataTable? table, string internalName)
    {
        if (table?.Columns.Contains(internalName) == true)
        {
            DataColumn column = table.Columns[internalName]!;
            return string.IsNullOrEmpty(column.Caption) ? column.ColumnName : column.Caption;
        }

        return internalName;
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
