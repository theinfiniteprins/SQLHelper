using SqlHelper.Core.Model;
using SqlHelper.Core.Registry;
using SqlHelper.Core.Tests.Fakes;

namespace SqlHelper.Core.Tests.Registry;

public sealed class RegistryStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public RegistryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sqlhelper-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "registry.dat");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private RegistryStore NewStore() => new(_file, new ReversibleTestProtector());

    [Fact]
    public void Load_with_no_file_returns_an_empty_registry()
    {
        ConnectionRegistry reg = NewStore().Load();

        Assert.Empty(reg.Targets);
        Assert.Empty(reg.Groups);
    }

    [Fact]
    public void Save_then_Load_round_trips_targets_secrets_and_groups()
    {
        RegistryStore store = NewStore();
        ConnectionRegistry reg = store.Load();

        var target = new DatabaseTarget
        {
            ClientName = "Acme", ServerName = "sql-01", DatabaseName = "AcmeDb",
            AuthMode = AuthMode.SqlLogin, UserId = "app_user", Environment = ServerEnvironment.Production,
            Tags = { "eu", "pilot" },
        };
        reg.Upsert(target, "p@ss");
        reg.UpsertGroup(new TargetGroup { Name = "Pilot", TargetIds = { target.Id }, Description = "first wave" });
        store.Save(reg);

        ConnectionRegistry reloaded = NewStore().Load();

        Assert.Single(reloaded.Targets);
        DatabaseTarget t = reloaded.Targets[0];
        Assert.Equal("Acme", t.ClientName);
        Assert.Equal(["eu", "pilot"], t.Tags);
        Assert.Equal("p@ss", reloaded.ResolvePassword(t.Id));
        Assert.Equal("first wave", reloaded.Groups[0].Description);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        RegistryStore store = NewStore();
        store.Save(store.Load());

        Assert.Empty(Directory.GetFiles(_dir).Where(f => f.Contains(".tmp-", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_corrupted_file_is_reported_clearly()
    {
        RegistryStore store = NewStore();
        store.Save(store.Load());

        byte[] bytes = File.ReadAllBytes(_file);
        bytes[0] ^= 0xFF;
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(_file, bytes);

        Assert.Throws<InvalidDataException>(() => NewStore().Load());
    }
}
