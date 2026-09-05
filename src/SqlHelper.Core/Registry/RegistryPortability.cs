using System.Text;
using System.Text.Json;
using SqlHelper.Core.Model;
using SqlHelper.Core.Serialization;

namespace SqlHelper.Core.Registry;

/// <summary>One database in a shared list. Deliberately has no password field of any kind.</summary>
public sealed record PortableTarget
{
    public required string ClientName { get; init; }

    public required string ServerName { get; init; }

    public required string DatabaseName { get; init; }

    public AuthMode AuthMode { get; init; }

    public string? UserId { get; init; }

    public ServerEnvironment Environment { get; init; } = ServerEnvironment.Production;

    public bool Enabled { get; init; } = true;

    public bool TrustServerCertificate { get; init; }

    public string? Notes { get; init; }

    public List<string> Tags { get; init; } = [];
}

/// <summary>The shape of an exported file. Plain JSON, so anyone can read exactly what they are sharing.</summary>
public sealed record PortableRegistry
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>States in the file itself that passwords were never included.</summary>
    public string Note { get; init; } = RegistryPortability.NoPasswordNotice;

    public string? ExportedBy { get; init; }

    public DateTimeOffset ExportedUtc { get; init; } = DateTimeOffset.UtcNow;

    public List<PortableTarget> Databases { get; init; } = [];

    public List<TargetGroup> Groups { get; init; } = [];
}

public sealed record ImportOutcome(int Added, int Updated, int Skipped, IReadOnlyList<string> Notes);

/// <summary>
/// Shares the list of databases between machines — server names, database names, logins,
/// environments — as readable JSON.
///
/// Passwords are never written. That is not just a convention here: the export type has no field
/// capable of carrying one, so there is nothing to leak even by mistake. Encrypted password blobs
/// would be worthless elsewhere in any case, since Windows ties them to one account on one
/// machine; whoever receives the file enters their own passwords once.
/// </summary>
public static class RegistryPortability
{
    /// <summary>
    /// How an incoming entry is matched to one already here. Client name alone is not enough:
    /// the same client can legitimately have several databases (production and reporting, say),
    /// and matching on the name alone would merge them into one — or, on an encrypted import,
    /// hand one database's password to another. Server and database name make it unambiguous.
    /// </summary>
    public static string IdentityKey(string clientName, string serverName, string databaseName) =>
        string.Join('', clientName.Trim(), serverName.Trim(), databaseName.Trim()).ToUpperInvariant();

    public static string IdentityKey(DatabaseTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return IdentityKey(target.ClientName, target.ServerName, target.DatabaseName);
    }

    public static string IdentityKey(PortableTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return IdentityKey(target.ClientName, target.ServerName, target.DatabaseName);
    }

    public const string NoPasswordNotice =
        "This file lists databases only. No passwords are included - whoever imports it enters their own.";

    public static PortableRegistry BuildExport(ConnectionRegistry registry, string? exportedBy = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return new PortableRegistry
        {
            ExportedBy = exportedBy,
            Databases = [.. registry.Targets.Select(t => new PortableTarget
            {
                ClientName = t.ClientName,
                ServerName = t.ServerName,
                DatabaseName = t.DatabaseName,
                AuthMode = t.AuthMode,
                UserId = t.UserId,
                Environment = t.Environment,
                Enabled = t.Enabled,
                TrustServerCertificate = t.TrustServerCertificate,
                Notes = t.Notes,
                Tags = [.. t.Tags],
            })],
            Groups = [.. registry.Groups.Select(g => new TargetGroup
            {
                Name = g.Name,
                Description = g.Description,
            })],
        };
    }

    public static void ExportToFile(ConnectionRegistry registry, string path, string? exportedBy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json = JsonSerializer.Serialize(BuildExport(registry, exportedBy), Json.Options);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    public static PortableRegistry ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json = File.ReadAllText(path);

        PortableRegistry? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PortableRegistry>(json, Json.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"That file is not a SqlHelper database list: {ex.Message}", ex);
        }

        if (parsed is null)
        {
            throw new InvalidDataException("That file is empty.");
        }

        if (parsed.Version > PortableRegistry.CurrentVersion)
        {
            throw new InvalidDataException(
                $"That file was written by a newer version of SqlHelper (format {parsed.Version}).");
        }

        return parsed;
    }

    /// <summary>
    /// Merges an exported list into this machine's registry, matching on client name. Existing
    /// entries keep whatever password is already stored here; a SQL login arriving without one
    /// has to be given a password on this machine before it can be used.
    /// </summary>
    public static ImportOutcome Import(ConnectionRegistry registry, PortableRegistry imported, bool overwriteExisting)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(imported);

        var notes = new List<string>();
        int added = 0;
        int updated = 0;
        int skipped = 0;

        foreach (PortableTarget incoming in imported.Databases)
        {
            if (string.IsNullOrWhiteSpace(incoming.ClientName)
                || string.IsNullOrWhiteSpace(incoming.ServerName)
                || string.IsNullOrWhiteSpace(incoming.DatabaseName))
            {
                skipped++;
                notes.Add($"Skipped an entry with missing details ({incoming.ClientName}).");
                continue;
            }

            string incomingKey = IdentityKey(incoming);
            DatabaseTarget? existing = registry.Targets.FirstOrDefault(
                t => IdentityKey(t) == incomingKey);

            if (existing is not null && !overwriteExisting)
            {
                skipped++;
                notes.Add($"{incoming.ClientName} ({incoming.DatabaseName} on {incoming.ServerName}) is already registered, so it was left as it is.");
                continue;
            }

            var target = new DatabaseTarget
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                ClientName = incoming.ClientName.Trim(),
                ServerName = incoming.ServerName.Trim(),
                DatabaseName = incoming.DatabaseName.Trim(),
                AuthMode = incoming.AuthMode,
                UserId = string.IsNullOrWhiteSpace(incoming.UserId) ? null : incoming.UserId.Trim(),
                Environment = incoming.Environment,
                Enabled = incoming.Enabled,
                TrustServerCertificate = incoming.TrustServerCertificate,
                Notes = incoming.Notes,
                Tags = [.. incoming.Tags],
            };

            registry.Upsert(target);

            if (existing is null)
            {
                added++;
            }
            else
            {
                updated++;
            }

            if (target.AuthMode == AuthMode.SqlLogin && !registry.HasPassword(target.Id))
            {
                notes.Add($"{target.ClientName} uses a SQL login - set its password before using it.");
            }
        }

        foreach (TargetGroup group in imported.Groups.Where(g => !string.IsNullOrWhiteSpace(g.Name)))
        {
            // Group membership is stored by id, which means nothing on another machine, so only
            // the group itself comes across.
            if (!registry.Groups.Any(g => string.Equals(g.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
            {
                registry.UpsertGroup(new TargetGroup { Name = group.Name, Description = group.Description });
            }
        }

        return new ImportOutcome(added, updated, skipped, notes);
    }
}
