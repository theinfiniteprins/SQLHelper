using SqlHelper.Core.Model;
using SqlHelper.Core.Registry;
using SqlHelper.Core.Tests.Fakes;

namespace SqlHelper.Core.Tests.Registry;

public sealed class ConnectionRegistryTests
{
    private static ConnectionRegistry NewRegistry() => ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());

    private static DatabaseTarget SqlTarget(string client = "Acme") => new()
    {
        ClientName = client,
        ServerName = "sql-01",
        DatabaseName = "AcmeDb",
        AuthMode = AuthMode.SqlLogin,
        UserId = "app_user",
    };

    [Fact]
    public void Upsert_adds_then_replaces_by_id()
    {
        ConnectionRegistry reg = NewRegistry();
        DatabaseTarget target = SqlTarget();

        reg.Upsert(target, "pw1");
        Assert.Single(reg.Targets);

        target.DatabaseName = "AcmeDb2";
        reg.Upsert(target);

        Assert.Single(reg.Targets);
        Assert.Equal("AcmeDb2", reg.Targets[0].DatabaseName);
    }

    [Fact]
    public void Stored_password_round_trips()
    {
        ConnectionRegistry reg = NewRegistry();
        DatabaseTarget target = SqlTarget();

        reg.Upsert(target, "S3cr3t!");

        Assert.True(reg.HasPassword(target.Id));
        Assert.Equal("S3cr3t!", reg.ResolvePassword(target.Id));
    }

    [Fact]
    public void Switching_to_windows_auth_drops_the_stored_password()
    {
        ConnectionRegistry reg = NewRegistry();
        DatabaseTarget target = SqlTarget();
        reg.Upsert(target, "pw");

        target.AuthMode = AuthMode.WindowsIntegrated;
        reg.Upsert(target);

        Assert.False(reg.HasPassword(target.Id));
    }

    [Fact]
    public void SetPassword_on_a_windows_auth_target_throws()
    {
        ConnectionRegistry reg = NewRegistry();
        var target = new DatabaseTarget
        {
            ClientName = "W", ServerName = "s", DatabaseName = "d", AuthMode = AuthMode.WindowsIntegrated,
        };
        reg.Upsert(target);

        Assert.Throws<InvalidOperationException>(() => reg.SetPassword(target.Id, "x"));
    }

    [Fact]
    public void Remove_deletes_target_secret_and_group_membership()
    {
        ConnectionRegistry reg = NewRegistry();
        DatabaseTarget target = SqlTarget();
        reg.Upsert(target, "pw");
        reg.UpsertGroup(new TargetGroup { Name = "All", TargetIds = { target.Id } });

        Assert.True(reg.Remove(target.Id));

        Assert.Empty(reg.Targets);
        Assert.False(reg.HasPassword(target.Id));
        Assert.Empty(reg.ResolveGroup("All"));
    }

    [Fact]
    public void Validate_flags_duplicate_client_names()
    {
        ConnectionRegistry reg = NewRegistry();
        reg.Upsert(SqlTarget("Acme"), "pw");
        reg.Upsert(SqlTarget("acme"), "pw");

        Assert.Contains(reg.Validate(), m => m.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_flags_sql_login_without_password()
    {
        ConnectionRegistry reg = NewRegistry();
        reg.Upsert(SqlTarget());

        Assert.Contains(reg.Validate(), m => m.Contains("no stored password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_flags_trust_server_certificate()
    {
        ConnectionRegistry reg = NewRegistry();
        DatabaseTarget target = SqlTarget();
        target.TrustServerCertificate = true;
        reg.Upsert(target, "pw");

        Assert.Contains(reg.Validate(), m => m.Contains("trusts any server certificate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveGroup_returns_members_in_declared_order_and_skips_deleted_ids()
    {
        ConnectionRegistry reg = NewRegistry();
        DatabaseTarget a = SqlTarget("A");
        DatabaseTarget b = SqlTarget("B");
        reg.Upsert(a, "pw");
        reg.Upsert(b, "pw");

        var ghost = Guid.NewGuid();
        reg.UpsertGroup(new TargetGroup { Name = "Ordered", TargetIds = { b.Id, ghost, a.Id } });

        IReadOnlyList<DatabaseTarget> resolved = reg.ResolveGroup("Ordered");

        Assert.Equal(["B", "A"], resolved.Select(t => t.ClientName));
    }

    [Fact]
    public void Upsert_rejects_missing_required_fields()
    {
        ConnectionRegistry reg = NewRegistry();

        Assert.ThrowsAny<ArgumentException>(() => reg.Upsert(new DatabaseTarget
        {
            ClientName = " ", ServerName = "s", DatabaseName = "d",
        }));
    }
}
