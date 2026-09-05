namespace SqlHelper.Core.Execution;

public sealed record QueryColumn(string Name, string ClrTypeName, string SqlTypeName);

/// <summary>One result set (grid) from a query.</summary>
public sealed class QueryTable
{
    public required IReadOnlyList<QueryColumn> Columns { get; init; }

    /// <summary>Row values, boxed. <see langword="null"/> represents SQL NULL.</summary>
    public required IReadOnlyList<object?[]> Rows { get; init; }

    /// <summary>True when <see cref="Rows"/> was capped by the row limit and more rows exist.</summary>
    public bool Truncated { get; init; }
}

/// <summary>Everything one query produced on one database: result grids plus server messages.</summary>
public sealed class QueryResult
{
    public List<QueryTable> Tables { get; } = [];

    /// <summary>PRINT output and informational messages, in order received.</summary>
    public List<string> Messages { get; } = [];

    public int TotalRows => Tables.Sum(t => t.Rows.Count);
}

/// <summary>Rows affected by one executed batch of a non-query script.</summary>
public sealed record BatchOutcome(int BatchNumber, int StartLine, int RowsAffected, string Text);

/// <summary>Result of running an INSERT/UPDATE/DELETE script (or its dry run) on one database.</summary>
public sealed class NonQueryResult
{
    public List<BatchOutcome> Batches { get; } = [];

    public List<string> Messages { get; } = [];

    /// <summary>True when everything executed inside a transaction that was then rolled back (a dry run, or an aborted run).</summary>
    public bool WasRolledBack { get; set; }

    public int TotalRowsAffected => Batches.Sum(b => Math.Max(b.RowsAffected, 0));
}
