using SqlHelper.Core.Orchestration;

namespace SqlHelper.App.ViewModels;

/// <summary>One database's outcome on the Deploy Object / Patch screens.</summary>
public sealed class DeployResultRow
{
    public required string ClientName { get; init; }

    public required bool Success { get; init; }

    public required string Method { get; init; }

    public string? Error { get; init; }

    public string? BeforeHash { get; init; }

    public string? AfterHash { get; init; }

    public int SignatureChangeCount { get; init; }

    public string Status => Success ? "✓ Succeeded" : "✕ Failed";

    public static DeployResultRow From(DeployTargetResult r) => new()
    {
        ClientName = r.Target.ClientName,
        Success = r.Succeeded,
        Method = r.Method,
        Error = r.Error,
        BeforeHash = Short(r.BeforeHash),
        AfterHash = Short(r.AfterHash),
        SignatureChangeCount = r.SignatureChanges.Count,
    };

    private static string? Short(string? hash) => hash is { Length: >= 8 } ? hash[..8] : hash;
}
