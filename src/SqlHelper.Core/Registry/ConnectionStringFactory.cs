using Microsoft.Data.SqlClient;
using SqlHelper.Core.Model;

namespace SqlHelper.Core.Registry;

/// <summary>Builds a SqlClient connection string from a <see cref="DatabaseTarget"/> and an optional password.</summary>
public static class ConnectionStringFactory
{
    /// <param name="password">Required for <see cref="AuthMode.SqlLogin"/>; ignored otherwise.</param>
    public static string Build(DatabaseTarget target, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = target.ServerName,
            InitialCatalog = target.DatabaseName,
            ConnectTimeout = target.ConnectTimeoutSeconds,
            CommandTimeout = target.CommandTimeoutSeconds,
            Encrypt = target.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = target.TrustServerCertificate,
            ApplicationName = $"SqlHelper/{AppInfo.Version}",
            Pooling = true,
            MultipleActiveResultSets = false,
            ApplicationIntent = ApplicationIntent.ReadWrite,
        };

        switch (target.AuthMode)
        {
            case AuthMode.WindowsIntegrated:
                builder.IntegratedSecurity = true;
                break;

            case AuthMode.SqlLogin:
                if (string.IsNullOrWhiteSpace(target.UserId))
                {
                    throw new InvalidOperationException($"Target '{target.ClientName}' is a SQL login but has no user id.");
                }

                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException($"Target '{target.ClientName}' is a SQL login but no password was supplied.");
                }

                builder.IntegratedSecurity = false;
                builder.UserID = target.UserId;
                builder.Password = password;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target.AuthMode, "Unknown auth mode.");
        }

        return builder.ConnectionString;
    }
}
