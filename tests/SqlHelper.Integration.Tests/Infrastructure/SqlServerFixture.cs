using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using SqlHelper.Core.Execution;
using SqlHelper.Core.Model;

namespace SqlHelper.Integration.Tests.Infrastructure;

/// <summary>
/// Creates a throwaway database on a local SQL Server for the duration of the test class, and
/// drops it afterwards. The server defaults to <c>localhost</c> with Windows authentication;
/// override with the <c>SQLHELPER_TEST_SERVER</c> environment variable.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly string _server = Environment.GetEnvironmentVariable("SQLHELPER_TEST_SERVER") ?? "localhost";

    public string DatabaseName { get; } = "sqlhelper_it_" + Guid.NewGuid().ToString("N")[..12];

    public DatabaseTarget Target { get; private set; } = null!;

    public ITargetConnectionFactory Connections { get; private set; } = null!;

    public SqlExecutor Executor { get; private set; } = null!;

    private readonly ConcurrentBag<string> _extraDatabases = [];

    /// <summary>Creates a second (or third, ...) throwaway database, for tests that need genuinely separate "client" databases.</summary>
    public async Task<DatabaseTarget> CreateClientDatabaseAsync(string clientName)
    {
        string dbName = "sqlhelper_it_" + Guid.NewGuid().ToString("N")[..12];
        await ExecuteOnMasterAsync($"CREATE DATABASE [{dbName}];").ConfigureAwait(false);
        _extraDatabases.Add(dbName);

        return new DatabaseTarget
        {
            ClientName = clientName,
            ServerName = _server,
            DatabaseName = dbName,
            AuthMode = AuthMode.WindowsIntegrated,
            Environment = ServerEnvironment.Development,
            CommandTimeoutSeconds = 30,
        };
    }

    /// <summary>Runs setup SQL against a specific target (as opposed to <see cref="ExecAsync"/>, which always targets <see cref="Target"/>).</summary>
    public async Task ExecOnAsync(DatabaseTarget target, string sql)
    {
        await using SqlConnection connection = await Connections.OpenAsync(target, CancellationToken.None).ConfigureAwait(false);
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task InitializeAsync()
    {
        await ExecuteOnMasterAsync($"CREATE DATABASE [{DatabaseName}];").ConfigureAwait(false);

        Target = new DatabaseTarget
        {
            ClientName = "IntegrationTest",
            ServerName = _server,
            DatabaseName = DatabaseName,
            AuthMode = AuthMode.WindowsIntegrated,
            Environment = ServerEnvironment.Development,
            CommandTimeoutSeconds = 30,
        };
        Connections = new DirectConnectionFactory();
        Executor = new SqlExecutor(Connections);
    }

    public async Task DisposeAsync()
    {
        foreach (string dbName in _extraDatabases.Append(DatabaseName))
        {
            await ExecuteOnMasterAsync(
                $"IF DB_ID('{dbName}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{dbName}]; END").ConfigureAwait(false);
        }
    }

    /// <summary>Runs setup SQL (possibly multi-statement, no GO) against the test database.</summary>
    public async Task ExecAsync(string sql)
    {
        await using var connection = new SqlConnection(MasterlessConnectionString());
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task ExecuteOnMasterAsync(string sql)
    {
        var builder = BaseBuilder();
        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string MasterlessConnectionString()
    {
        var builder = BaseBuilder();
        builder.InitialCatalog = DatabaseName;
        return builder.ConnectionString;
    }

    private SqlConnectionStringBuilder BaseBuilder() => new()
    {
        DataSource = _server,
        IntegratedSecurity = true,
        Encrypt = SqlConnectionEncryptOption.Optional,
        TrustServerCertificate = true,
        ConnectTimeout = 15,
    };

    private sealed class DirectConnectionFactory : ITargetConnectionFactory
    {
        public async Task<SqlConnection> OpenAsync(DatabaseTarget target, CancellationToken cancellationToken)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = target.ServerName,
                InitialCatalog = target.DatabaseName,
                IntegratedSecurity = true,
                Encrypt = SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = true,
                ConnectTimeout = target.ConnectTimeoutSeconds,
            };
            var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
