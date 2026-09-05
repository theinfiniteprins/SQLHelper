using System.Text;
using System.Text.Json;
using SqlHelper.Core;
using SqlHelper.Core.Model;
using SqlHelper.Core.Scripting;
using SqlHelper.Core.Serialization;

namespace SqlHelper.Core.Backup;

/// <summary>Creates the timestamped backup folder for one deployment and writes files into it.</summary>
public sealed class BackupWriter
{
    private readonly string _backupRoot;

    public BackupWriter(string backupRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        _backupRoot = backupRoot;
    }

    /// <summary>
    /// Creates the folder for one run. It is named for the moment it ran, with the change title
    /// appended only when one was given — the title is optional, so it can never be the thing
    /// that makes a backup findable.
    /// </summary>
    public BackupSession BeginSession(string? changeTitle, DateTimeOffset now)
    {
        string stamp = SafeName.TimestampFolder(now);
        string folderName = string.IsNullOrWhiteSpace(changeTitle)
            ? stamp
            : $"{stamp}__{SafeName.ForPath(changeTitle, 40)}";

        string folder = Path.Combine(_backupRoot, folderName);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "_rollback"));
        return new BackupSession(folder, stamp);
    }
}

/// <summary>A single deployment's backup folder. All paths returned are absolute.</summary>
public sealed class BackupSession
{
    public BackupSession(string folder, string timestamp)
    {
        Folder = folder;
        Timestamp = timestamp;
    }

    public string Folder { get; }

    /// <summary>The run timestamp, used to name each captured file.</summary>
    public string Timestamp { get; }

    public string RollbackFolder => Path.Combine(Folder, "_rollback");

    /// <summary>Writes the object's current definition to <c>&lt;client&gt;\&lt;schema&gt;.&lt;name&gt;.sql</c>.</summary>
    public async Task<string> CaptureAsync(
        DatabaseTarget target,
        ProgrammableObject module,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(module);

        if (!module.HasDefinition)
        {
            throw new InvalidOperationException(
                $"'{module.Name.Plain}' on '{target.ClientName}' has no readable definition (encrypted?) — cannot back it up.");
        }

        string clientFolder = EnsureClientFolder(target);
        // Named for the object and the moment it was taken - "dbo.usp_GetOrders_2026-09-05_02-30-15.sql".
        string path = Path.Combine(clientFolder, $"{module.FileStem}_{Timestamp}.sql");

        var content = new StringBuilder();
        content.Append(Header(target, module.Name.Plain, TSqlNormalizer.ExactKey(module.Definition), module.ModifiedServerTime));
        content.Append(ModuleScript.ToRunnableScript(module, forceCreateOrAlter: true));

        await File.WriteAllTextAsync(path, content.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>Records that the object did not exist before deployment (a brand-new object).</summary>
    public async Task<string> CaptureMissingAsync(
        DatabaseTarget target,
        ObjectName name,
        CancellationToken cancellationToken = default)
    {
        string clientFolder = EnsureClientFolder(target);
        string path = Path.Combine(clientFolder, $"{name.Schema}.{name.Name}_{Timestamp}.NEW.txt");

        string content = Header(target, name.Plain, beforeHash: null, modified: null) +
            $"This object did not exist on {target.ClientName} before this deployment.\r\n" +
            "Rolling back means dropping it.\r\n";

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>Writes a per-client rollback script that re-applies every captured definition (and drops new objects).</summary>
    public async Task<string> WriteRollbackAsync(
        DatabaseTarget target,
        IReadOnlyList<ProgrammableObject> previousDefinitions,
        IReadOnlyList<DroppedObject> newObjects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        string path = Path.Combine(RollbackFolder, $"rollback__{SafeName.ForPath(target.ClientName)}.sql");

        var content = new StringBuilder();
        content.Append($"/*  Rollback for {target.ClientName}  —  {target.ServerName}/{target.DatabaseName}\r\n");
        content.Append($"    Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} by SqlHelper {AppInfo.Version}\r\n");
        content.Append("    Re-applies each object's definition as it was immediately before the deployment.\r\n");
        content.Append("    Review before running.  */\r\n\r\n");

        foreach (ProgrammableObject module in previousDefinitions.Where(m => m.HasDefinition))
        {
            content.Append($"-- {module.Name.Plain}\r\n");
            content.Append(ModuleScript.ToRunnableScript(module, forceCreateOrAlter: true));
            content.Append("\r\n");
        }

        foreach (DroppedObject created in newObjects)
        {
            content.Append($"-- {created.Name.Plain} was new in this deployment; rollback drops it.\r\n");
            content.Append($"DROP {DropKeyword(created.Kind)} IF EXISTS {created.Name.Bracketed};\r\nGO\r\n\r\n");
        }

        await File.WriteAllTextAsync(path, content.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> WriteManifestAsync(DeploymentManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string path = Path.Combine(Folder, "_manifest.json");
        string json = JsonSerializer.Serialize(manifest, Json.Options);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return path;
    }

    private string EnsureClientFolder(DatabaseTarget target)
    {
        string path = Path.Combine(Folder, SafeName.ForPath(target.ClientName));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Header(DatabaseTarget target, string objectName, string? beforeHash, DateTime? modified)
    {
        var builder = new StringBuilder();
        builder.Append("/*  SqlHelper backup\r\n");
        builder.Append($"    Client     : {target.ClientName}\r\n");
        builder.Append($"    Server      : {target.ServerName}\r\n");
        builder.Append($"    Database    : {target.DatabaseName}\r\n");
        builder.Append($"    Environment : {target.Environment}\r\n");
        builder.Append($"    Object      : {objectName}\r\n");
        builder.Append($"    Captured    : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\r\n");
        if (modified is { } m)
        {
            builder.Append($"    Last change : {m:yyyy-MM-dd HH:mm:ss} (server time)\r\n");
        }

        if (beforeHash is not null)
        {
            builder.Append($"    Checksum    : {beforeHash}\r\n");
        }

        builder.Append($"    Tool        : SqlHelper {AppInfo.Version}\r\n");
        builder.Append($"    Operator    : {AppInfo.Operator} on {AppInfo.Machine}\r\n");
        builder.Append("*/\r\n\r\n");
        return builder.ToString();
    }

    private static string DropKeyword(ProgrammableObjectKind kind) => kind switch
    {
        ProgrammableObjectKind.StoredProcedure => "PROCEDURE",
        ProgrammableObjectKind.ScalarFunction or ProgrammableObjectKind.TableValuedFunction => "FUNCTION",
        ProgrammableObjectKind.View => "VIEW",
        ProgrammableObjectKind.Trigger => "TRIGGER",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>A newly created object, recorded so a rollback knows to drop it.</summary>
public sealed record DroppedObject(ObjectName Name, ProgrammableObjectKind Kind);
