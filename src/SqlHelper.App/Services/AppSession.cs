using SqlHelper.Core;
using SqlHelper.Core.Auditing;
using SqlHelper.Core.Credentials;
using SqlHelper.Core.Execution;
using SqlHelper.Core.Orchestration;
using SqlHelper.Core.Registry;

namespace SqlHelper.App.Services;

/// <summary>
/// Everything a screen needs, assembled once at startup after the registry is unlocked. Holds
/// the live, in-memory registry — every screen mutates <see cref="Registry"/> directly and calls
/// <see cref="Save"/> to persist.
/// </summary>
public sealed class AppSession
{
    public required AppPaths Paths { get; init; }

    public required ISecretProtector Protector { get; init; }

    public required RegistryStore Store { get; init; }

    public required ConnectionRegistry Registry { get; set; }

    public required AuditLog Audit { get; init; }

    public required ITargetConnectionFactory Connections { get; init; }

    public required DeploymentEngine Engine { get; init; }

    public void Save() => Store.Save(Registry);
}
