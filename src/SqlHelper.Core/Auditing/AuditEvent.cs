namespace SqlHelper.Core.Auditing;

/// <summary>What happened to one target within an audited operation.</summary>
public sealed record AuditTargetResult(
    string ClientName,
    string Server,
    string Database,
    string Environment,
    string Status,
    int? RowsAffected,
    string? Error,
    string? BeforeHash,
    string? AfterHash);

/// <summary>
/// One row of the audit trail: who ran what, against which databases, and what happened.
/// Written once per operation (not once per target) to keep the log compact and the file write
/// count low.
/// </summary>
public sealed record AuditEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public required string Operator { get; init; }

    public required string Machine { get; init; }

    /// <summary>"select" | "nonquery" | "nonquery-dry-run" | "deploy-object" | "patch-object".</summary>
    public required string Action { get; init; }

    public string? ChangeTitle { get; init; }

    public string? Ticket { get; init; }

    /// <summary>The statement or script that was run, verbatim, for after-the-fact review.</summary>
    public string? Statement { get; init; }

    public required IReadOnlyList<AuditTargetResult> Targets { get; init; }

    public TimeSpan Duration { get; init; }

    public int SucceededCount => Targets.Count(t => t.Status == "succeeded");

    public int FailedCount => Targets.Count(t => t.Status == "failed");
}
