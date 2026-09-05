using System.Text.Json.Serialization;

namespace SqlHelper.Core.Model;

/// <summary>
/// One client database the tool can operate on. Holds connection identity and metadata only —
/// never a plaintext password. The registry keeps the encrypted password separately, keyed by
/// <see cref="Id"/>.
/// </summary>
public sealed class DatabaseTarget
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Human label shown in every client-wise view. Required and unique within the registry.</summary>
    public required string ClientName { get; set; }

    /// <summary>Server / instance / <c>host,port</c>, exactly as typed into SSMS.</summary>
    public required string ServerName { get; set; }

    public required string DatabaseName { get; set; }

    public AuthMode AuthMode { get; set; } = AuthMode.WindowsIntegrated;

    /// <summary>SQL login name. Ignored when <see cref="AuthMode"/> is <see cref="AuthMode.WindowsIntegrated"/>.</summary>
    public string? UserId { get; set; }

    public ServerEnvironment Environment { get; set; } = ServerEnvironment.Production;

    /// <summary>When false the target is skipped by every operation and shown greyed-out.</summary>
    public bool Enabled { get; set; } = true;

    public List<string> Tags { get; init; } = [];

    public string? Notes { get; set; }

    // --- connection tuning; conservative, SSMS-like defaults ---

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>Encrypt the connection (TLS in transit). On by default and strongly recommended.</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>
    /// Accept a server certificate that does not chain to a trusted root. Only for servers with
    /// self-signed certs; every use is written to the audit log as a risk acknowledgement.
    /// </summary>
    public bool TrustServerCertificate { get; set; }

    [JsonIgnore]
    public bool RequiresPassword => AuthMode == AuthMode.SqlLogin;

    [JsonIgnore]
    public string QualifiedName => $"{ServerName}/{DatabaseName}";

    public DatabaseTarget Clone() => new()
    {
        Id = Id,
        ClientName = ClientName,
        ServerName = ServerName,
        DatabaseName = DatabaseName,
        AuthMode = AuthMode,
        UserId = UserId,
        Environment = Environment,
        Enabled = Enabled,
        Tags = [.. Tags],
        Notes = Notes,
        ConnectTimeoutSeconds = ConnectTimeoutSeconds,
        CommandTimeoutSeconds = CommandTimeoutSeconds,
        Encrypt = Encrypt,
        TrustServerCertificate = TrustServerCertificate,
    };
}
