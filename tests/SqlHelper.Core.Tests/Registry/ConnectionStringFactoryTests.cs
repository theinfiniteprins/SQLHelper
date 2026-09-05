using Microsoft.Data.SqlClient;
using SqlHelper.Core.Model;
using SqlHelper.Core.Registry;

namespace SqlHelper.Core.Tests.Registry;

public sealed class ConnectionStringFactoryTests
{
    private static DatabaseTarget Base() => new()
    {
        ClientName = "Acme", ServerName = "sql-01\\INST", DatabaseName = "AcmeDb",
    };

    [Fact]
    public void Windows_auth_uses_integrated_security_and_no_password()
    {
        DatabaseTarget target = Base();
        target.AuthMode = AuthMode.WindowsIntegrated;

        var csb = new SqlConnectionStringBuilder(ConnectionStringFactory.Build(target));

        Assert.True(csb.IntegratedSecurity);
        Assert.Equal("AcmeDb", csb.InitialCatalog);
        Assert.Equal("sql-01\\INST", csb.DataSource);
        Assert.Empty(csb.Password);
    }

    [Fact]
    public void Sql_login_includes_user_and_password()
    {
        DatabaseTarget target = Base();
        target.AuthMode = AuthMode.SqlLogin;
        target.UserId = "app_user";

        var csb = new SqlConnectionStringBuilder(ConnectionStringFactory.Build(target, "p@ss w0rd"));

        Assert.False(csb.IntegratedSecurity);
        Assert.Equal("app_user", csb.UserID);
        Assert.Equal("p@ss w0rd", csb.Password);
    }

    [Fact]
    public void Sql_login_without_password_throws()
    {
        DatabaseTarget target = Base();
        target.AuthMode = AuthMode.SqlLogin;
        target.UserId = "app_user";

        Assert.Throws<InvalidOperationException>(() => ConnectionStringFactory.Build(target));
    }

    [Fact]
    public void Sql_login_without_user_id_throws()
    {
        DatabaseTarget target = Base();
        target.AuthMode = AuthMode.SqlLogin;

        Assert.Throws<InvalidOperationException>(() => ConnectionStringFactory.Build(target, "pw"));
    }

    [Fact]
    public void Encrypt_flag_maps_to_mandatory_or_optional()
    {
        DatabaseTarget on = Base();
        on.Encrypt = true;
        DatabaseTarget off = Base();
        off.Encrypt = false;

        Assert.Equal(
            SqlConnectionEncryptOption.Mandatory,
            new SqlConnectionStringBuilder(ConnectionStringFactory.Build(on)).Encrypt);
        Assert.Equal(
            SqlConnectionEncryptOption.Optional,
            new SqlConnectionStringBuilder(ConnectionStringFactory.Build(off)).Encrypt);
    }

    [Fact]
    public void Application_name_identifies_the_tool()
    {
        var csb = new SqlConnectionStringBuilder(ConnectionStringFactory.Build(Base()));

        Assert.StartsWith("SqlHelper/", csb.ApplicationName);
    }
}
