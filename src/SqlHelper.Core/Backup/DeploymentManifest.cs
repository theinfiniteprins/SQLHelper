namespace SqlHelper.Core.Backup;

/// <summary>What happened to one object on one database during a deployment.</summary>
public sealed record ManifestEntry
{
    public required string Client { get; init; }

    public required string Server { get; init; }

    public required string Database { get; init; }

    public required string Environment { get; init; }

    public required string ObjectName { get; init; }

    public required string ObjectKind { get; init; }

    /// <summary>"replace", "patch-exact", "patch-fuzzy", "patch-manual", "new-object", "skipped-identical".</summary>
    public required string Method { get; init; }

    /// <summary>"succeeded", "failed", "skipped", "cancelled".</summary>
    public required string Result { get; init; }

    public string? BeforeHash { get; init; }

    public string? AfterHash { get; init; }

    public string? BackupFile { get; init; }

    public string? RollbackFile { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<string> SignatureChanges { get; init; } = [];
}

/// <summary>The <c>_manifest.json</c> written into every backup folder.</summary>
public sealed record DeploymentManifest
{
    public required string ToolVersion { get; init; }

    public required string Operator { get; init; }

    public required string Machine { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? FinishedUtc { get; set; }

    public required string ChangeTitle { get; init; }

    public string? Ticket { get; init; }

    public string? Notes { get; init; }

    public List<ManifestEntry> Entries { get; init; } = [];

    public int Succeeded => Entries.Count(e => e.Result == "succeeded");

    public int Failed => Entries.Count(e => e.Result == "failed");
}
