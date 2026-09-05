using System.Reflection;

namespace SqlHelper.Core;

/// <summary>Identity of the running tool and operator, stamped into backups and the audit log.</summary>
public static class AppInfo
{
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    /// <summary><c>DOMAIN\user</c> of the Windows account running the tool.</summary>
    public static string Operator { get; } = $"{Environment.UserDomainName}\\{Environment.UserName}";

    public static string Machine { get; } = Environment.MachineName;
}

/// <summary>Resolves the on-disk locations the tool uses. Everything lives under one root, per user.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
        RegistryFile = Path.Combine(root, "registry.sqlhelper.dat");
        AuditDirectory = Path.Combine(root, "audit");
        DefaultBackupRoot = Path.Combine(root, "DeploymentBackups");
    }

    public string Root { get; }

    /// <summary>Encrypted connection registry (targets, groups, protected passwords).</summary>
    public string RegistryFile { get; }

    /// <summary>Append-only audit log directory (one JSONL file per month).</summary>
    public string AuditDirectory { get; }

    /// <summary>Where object-deployment backups are written unless the operator picks another folder.</summary>
    public string DefaultBackupRoot { get; }

    /// <summary><c>%APPDATA%\SqlHelper</c> — not beside the executable, never in source control.</summary>
    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlHelper"));

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(AuditDirectory);
    }
}
