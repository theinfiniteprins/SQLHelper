using Microsoft.Data.SqlClient;
using SqlHelper.Core.Execution;
using SqlHelper.Core.Model;
using SqlHelper.Core.Registry;

namespace SqlHelper.App.Services;

/// <summary>
/// Opens a connection using a password supplied directly rather than read from the registry —
/// so "Test connection" can check credentials the operator has typed but not yet saved.
/// </summary>
public sealed class AdHocConnectionFactory : ITargetConnectionFactory
{
    private readonly string? _password;

    public AdHocConnectionFactory(string? password) => _password = password;

    public async Task<SqlConnection> OpenAsync(DatabaseTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        string connectionString = ConnectionStringFactory.Build(
            target, target.AuthMode == AuthMode.SqlLogin ? _password : null);

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
