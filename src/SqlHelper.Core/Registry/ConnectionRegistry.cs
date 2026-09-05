using SqlHelper.Core.Credentials;
using SqlHelper.Core.Model;

namespace SqlHelper.Core.Registry;

/// <summary>
/// In-memory view of the connection registry: the list of targets, their encrypted passwords,
/// and named groups. All mutation goes through here so validation and secret handling stay in
/// one place. Persisted by <see cref="RegistryStore"/>.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly RegistryDocument _doc;
    private readonly ISecretProtector _protector;

    public ConnectionRegistry(RegistryDocument document, ISecretProtector protector)
    {
        _doc = document ?? throw new ArgumentNullException(nameof(document));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public static ConnectionRegistry CreateEmpty(ISecretProtector protector) => new(new RegistryDocument(), protector);

    public IReadOnlyList<DatabaseTarget> Targets => _doc.Targets;

    public IReadOnlyList<TargetGroup> Groups => _doc.Groups;

    internal RegistryDocument Document => _doc;

    public DatabaseTarget? Find(Guid id) => _doc.Targets.FirstOrDefault(t => t.Id == id);

    /// <summary>Adds or replaces a target. Supplying <paramref name="password"/> stores it encrypted.</summary>
    public void Upsert(DatabaseTarget target, ReadOnlySpan<char> password = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateShape(target);

        int index = _doc.Targets.FindIndex(t => t.Id == target.Id);
        if (index < 0)
        {
            _doc.Targets.Add(target);
        }
        else
        {
            _doc.Targets[index] = target;
        }

        string key = target.Id.ToString();
        if (target.AuthMode != AuthMode.SqlLogin)
        {
            _doc.Secrets.Remove(key); // Windows auth stores no password.
        }
        else if (!password.IsEmpty)
        {
            _doc.Secrets[key] = Convert.ToBase64String(_protector.Protect(password));
        }
    }

    public bool Remove(Guid id)
    {
        int index = _doc.Targets.FindIndex(t => t.Id == id);
        if (index < 0)
        {
            return false;
        }

        _doc.Targets.RemoveAt(index);
        _doc.Secrets.Remove(id.ToString());
        foreach (TargetGroup group in _doc.Groups)
        {
            group.TargetIds.RemoveAll(g => g == id);
        }

        return true;
    }

    public void SetPassword(Guid id, ReadOnlySpan<char> password)
    {
        DatabaseTarget target = Find(id) ?? throw new InvalidOperationException($"No target with id {id}.");
        if (target.AuthMode != AuthMode.SqlLogin)
        {
            throw new InvalidOperationException($"Target '{target.ClientName}' uses Windows authentication and takes no password.");
        }

        _doc.Secrets[id.ToString()] = Convert.ToBase64String(_protector.Protect(password));
    }

    public bool HasPassword(Guid id) => _doc.Secrets.ContainsKey(id.ToString());

    /// <summary>Decrypts and returns the stored password. Throws if none is stored or the blob is not readable by this account.</summary>
    public string ResolvePassword(Guid id)
    {
        if (!_doc.Secrets.TryGetValue(id.ToString(), out string? b64))
        {
            throw new InvalidOperationException($"No stored password for target {id}.");
        }

        return _protector.Unprotect(Convert.FromBase64String(b64));
    }

    // --- groups ---

    public void UpsertGroup(TargetGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(group.Name);

        int index = _doc.Groups.FindIndex(g => string.Equals(g.Name, group.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            _doc.Groups.Add(group);
        }
        else
        {
            _doc.Groups[index] = group;
        }
    }

    public bool RemoveGroup(string name) => _doc.Groups.RemoveAll(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;

    public IReadOnlyList<DatabaseTarget> ResolveGroup(string name)
    {
        TargetGroup? group = _doc.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            return [];
        }

        return group.TargetIds
            .Select(Find)
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();
    }

    /// <summary>
    /// Whole-registry checks: duplicate client names, and SQL logins with no stored password.
    /// Returns a human-readable list; empty means clean.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];

        IEnumerable<IGrouping<string, DatabaseTarget>> dupes = _doc.Targets
            .GroupBy(t => t.ClientName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (IGrouping<string, DatabaseTarget> dup in dupes)
        {
            problems.Add($"Client name '{dup.Key}' is used by {dup.Count()} targets; names must be unique.");
        }

        foreach (DatabaseTarget target in _doc.Targets)
        {
            if (target.AuthMode == AuthMode.SqlLogin && string.IsNullOrWhiteSpace(target.UserId))
            {
                problems.Add($"'{target.ClientName}' uses a SQL login but has no user id.");
            }

            if (target.AuthMode == AuthMode.SqlLogin && !HasPassword(target.Id))
            {
                problems.Add($"'{target.ClientName}' uses a SQL login but has no stored password.");
            }

            if (target.TrustServerCertificate)
            {
                problems.Add($"'{target.ClientName}' trusts any server certificate — acceptable only for a known self-signed cert.");
            }
        }

        return problems;
    }

    private static void ValidateShape(DatabaseTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target.ClientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.ServerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.DatabaseName);

        if (target.ConnectTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "Connect timeout must be positive.");
        }

        if (target.CommandTimeoutSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "Command timeout cannot be negative.");
        }
    }
}
