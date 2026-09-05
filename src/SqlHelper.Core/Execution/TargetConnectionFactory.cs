using Microsoft.Data.SqlClient;
using SqlHelper.Core.Model;
using SqlHelper.Core.Registry;

namespace SqlHelper.Core.Execution;

/// <summary>Opens a live connection for a target. Abstracted so the executor can be tested with a fake.</summary>
public interface ITargetConnectionFactory
{
    Task<SqlConnection> OpenAsync(DatabaseTarget target, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves a target's password from the encrypted registry (only for SQL logins), builds the
/// connection string, and opens the connection. The decrypted password lives only for the
/// duration of this call.
/// </summary>
public sealed class RegistryConnectionFactory : ITargetConnectionFactory
{
    private readonly ConnectionRegistry _registry;

    public RegistryConnectionFactory(ConnectionRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public async Task<SqlConnection> OpenAsync(DatabaseTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        string? password = null;
        if (target.AuthMode == AuthMode.SqlLogin)
        {
            password = _registry.ResolvePassword(target.Id);
        }

        string connectionString = ConnectionStringFactory.Build(target, password);

        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
