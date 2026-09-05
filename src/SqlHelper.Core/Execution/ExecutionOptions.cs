using SqlHelper.Core.Guard;

namespace SqlHelper.Core.Execution;

public sealed record QueryExecutionOptions
{
    /// <summary>Hard cap on rows materialised per result set. Extra rows set <see cref="QueryTable.Truncated"/>.</summary>
    public int MaxRows { get; init; } = 100_000;

    /// <summary>Run the query inside a transaction that is always rolled back. On by default — a safety net behind the read-only guard.</summary>
    public bool WrapInRollbackTransaction { get; init; } = true;

    public static QueryExecutionOptions Default { get; } = new();
}

public enum NonQueryTransactionMode
{
    /// <summary>Each batch commits on its own as it runs (closest to SSMS default).</summary>
    None = 0,

    /// <summary>One transaction around the whole script: all batches commit together or none do.</summary>
    WholeScript = 1,
}

public sealed record NonQueryExecutionOptions
{
    /// <summary>Execute everything inside a transaction and roll it back. Reports rows affected without changing data.</summary>
    public bool DryRun { get; init; }

    public NonQueryTransactionMode TransactionMode { get; init; } = NonQueryTransactionMode.WholeScript;

    /// <summary>Stop at the first failing batch (and roll back if in a transaction). When false, keep going and report each failure.</summary>
    public bool StopOnError { get; init; } = true;

    public TSqlCompatibility Compatibility { get; init; } = TSqlParsing.Default;

    public static NonQueryExecutionOptions Default { get; } = new();

    public NonQueryExecutionOptions AsDryRun() => this with { DryRun = true, TransactionMode = NonQueryTransactionMode.WholeScript };
}
