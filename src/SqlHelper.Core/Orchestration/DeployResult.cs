using SqlHelper.Core.Model;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Orchestration;

public sealed record DeployTargetResult(
    DatabaseTarget Target,
    bool Succeeded,
    string Method,
    string? Error,
    string? BackupFile,
    string? BeforeHash,
    string? AfterHash,
    IReadOnlyList<string> SignatureChanges,
    string? ObjectKind = null)
{
    /// <summary>The object exactly as it was before this run — the material a rollback script is built from. Null when the object was new.</summary>
    public ProgrammableObject? PreviousDefinition { get; init; }

    /// <summary>True when the object did not exist on this target before this run (so a rollback must drop it instead of restoring it).</summary>
    public bool WasNew { get; init; }
}

public sealed record DeployResult(string BackupFolder, string ManifestPath, IReadOnlyList<DeployTargetResult> Results)
{
    public int SucceededCount => Results.Count(r => r.Succeeded);

    public int FailedCount => Results.Count(r => !r.Succeeded);

    public IReadOnlyList<DatabaseTarget> FailedTargets => [.. Results.Where(r => !r.Succeeded).Select(r => r.Target)];
}
