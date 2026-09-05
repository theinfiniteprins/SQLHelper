using SqlHelper.Core.Model;

namespace SqlHelper.Core.Registry;

/// <summary>The serializable shape persisted to the encrypted registry file.</summary>
public sealed class RegistryDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<DatabaseTarget> Targets { get; init; } = [];

    /// <summary>Per-target SQL password, each an independent DPAPI blob, base64-encoded. Keyed by target id.</summary>
    public Dictionary<string, string> Secrets { get; init; } = [];

    public List<TargetGroup> Groups { get; init; } = [];
}
