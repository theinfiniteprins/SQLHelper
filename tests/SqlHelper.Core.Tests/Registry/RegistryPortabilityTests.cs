using System.Reflection;
using System.Text.Json;
using SqlHelper.Core.Model;
using SqlHelper.Core.Registry;
using SqlHelper.Core.Tests.Fakes;

namespace SqlHelper.Core.Tests.Registry;

public sealed class RegistryPortabilityTests : IDisposable
{
    private const string SecretPassword = "Sup3rSecret!ThisMustNeverBeExported";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlhelper-portability", Guid.NewGuid().ToString("N"));

    public RegistryPortabilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ConnectionRegistry RegistryWithSecrets()
    {
        ConnectionRegistry registry = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());
        registry.Upsert(
            new DatabaseTarget
            {
                ClientName = "Acme",
                ServerName = "sql-01",
                DatabaseName = "AcmeDb",
                AuthMode = AuthMode.SqlLogin,
                UserId = "app_user",
                Environment = ServerEnvironment.Production,
                TrustServerCertificate = true,
                Notes = "primary",
            },
            SecretPassword);

        registry.Upsert(new DatabaseTarget
        {
            ClientName = "Globex",
            ServerName = "sql-02",
            DatabaseName = "GlobexDb",
            AuthMode = AuthMode.WindowsIntegrated,
            Environment = ServerEnvironment.Test,
        });

        return registry;
    }

    // ---------------------------------------------------------------- security

    [Fact]
    public void The_exported_file_never_contains_a_password()
    {
        ConnectionRegistry registry = RegistryWithSecrets();
        string path = Path.Combine(_dir, "share.sqlhelper.json");

        RegistryPortability.ExportToFile(registry, path);
        string contents = File.ReadAllText(path);

        Assert.DoesNotContain(SecretPassword, contents, StringComparison.Ordinal);

        // The notice text mentions the word "passwords" on purpose, so look for a field that
        // could actually carry one rather than for the word itself.
        using JsonDocument document = JsonDocument.Parse(contents);
        foreach (JsonElement database in document.RootElement.GetProperty("Databases").EnumerateArray())
        {
            foreach (JsonProperty property in database.EnumerateObject())
            {
                Assert.DoesNotContain("password", property.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("secret", property.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void The_export_type_has_no_field_capable_of_carrying_a_credential()
    {
        // Belt and braces: even a future edit that tried to copy a password across would have
        // nowhere to put it. This test fails the moment such a field is introduced.
        string[] forbidden = ["password", "secret", "credential", "pwd", "token", "key"];

        var fields = typeof(PortableTarget).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(PortableRegistry).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => p.Name)
            .ToList();

        foreach (string name in fields)
        {
            Assert.DoesNotContain(forbidden, f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Importing_never_grants_a_password_that_was_not_already_on_this_machine()
    {
        ConnectionRegistry source = RegistryWithSecrets();
        string path = Path.Combine(_dir, "share.json");
        RegistryPortability.ExportToFile(source, path);

        ConnectionRegistry destination = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());
        RegistryPortability.Import(destination, RegistryPortability.ReadFile(path), overwriteExisting: false);

        DatabaseTarget acme = destination.Targets.Single(t => t.ClientName == "Acme");
        Assert.False(destination.HasPassword(acme.Id));
        Assert.Throws<InvalidOperationException>(() => destination.ResolvePassword(acme.Id));
    }

    [Fact]
    public void The_file_says_in_plain_words_that_it_holds_no_passwords()
    {
        ConnectionRegistry registry = RegistryWithSecrets();
        string path = Path.Combine(_dir, "share.json");
        RegistryPortability.ExportToFile(registry, path);

        Assert.Contains("No passwords are included", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- behaviour

    [Fact]
    public void Everything_except_the_password_survives_the_round_trip()
    {
        ConnectionRegistry source = RegistryWithSecrets();
        string path = Path.Combine(_dir, "share.json");
        RegistryPortability.ExportToFile(source, path, exportedBy: "tester");

        ConnectionRegistry destination = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());
        ImportOutcome outcome = RegistryPortability.Import(destination, RegistryPortability.ReadFile(path), overwriteExisting: false);

        Assert.Equal(2, outcome.Added);
        DatabaseTarget acme = destination.Targets.Single(t => t.ClientName == "Acme");
        Assert.Equal("sql-01", acme.ServerName);
        Assert.Equal("AcmeDb", acme.DatabaseName);
        Assert.Equal(AuthMode.SqlLogin, acme.AuthMode);
        Assert.Equal("app_user", acme.UserId);
        Assert.Equal(ServerEnvironment.Production, acme.Environment);
        Assert.True(acme.TrustServerCertificate);
        Assert.Equal("primary", acme.Notes);
    }

    [Fact]
    public void A_sql_login_arriving_without_a_password_is_called_out()
    {
        ConnectionRegistry source = RegistryWithSecrets();
        string path = Path.Combine(_dir, "share.json");
        RegistryPortability.ExportToFile(source, path);

        ConnectionRegistry destination = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());
        ImportOutcome outcome = RegistryPortability.Import(destination, RegistryPortability.ReadFile(path), overwriteExisting: false);

        Assert.Contains(outcome.Notes, n => n.Contains("Acme", StringComparison.Ordinal)
                                            && n.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Existing_entries_are_left_alone_unless_overwrite_is_asked_for()
    {
        ConnectionRegistry source = RegistryWithSecrets();
        string path = Path.Combine(_dir, "share.json");
        RegistryPortability.ExportToFile(source, path);

        ConnectionRegistry destination = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());
        destination.Upsert(new DatabaseTarget
        {
            ClientName = "Acme",
            ServerName = "sql-01",
            DatabaseName = "AcmeDb",
            Notes = "mine",
        });

        ImportOutcome outcome = RegistryPortability.Import(destination, RegistryPortability.ReadFile(path), overwriteExisting: false);

        Assert.Equal("mine", destination.Targets.Single(t => t.ClientName == "Acme").Notes);
        Assert.Equal(1, outcome.Skipped);
    }

    [Fact]
    public void An_entry_with_the_same_client_name_but_a_different_database_is_added_not_repointed()
    {
        // Two people can both have a client called "Acme" pointing at different databases.
        // Quietly repointing the local one at the incoming server would be a way to run a change
        // against the wrong database, so it must arrive as a separate entry instead.
        ConnectionRegistry source = RegistryWithSecrets();
        string path = Path.Combine(_dir, "share.json");
        RegistryPortability.ExportToFile(source, path);

        ConnectionRegistry destination = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());
        destination.Upsert(new DatabaseTarget
        {
            ClientName = "Acme",
            ServerName = "my-own-server",
            DatabaseName = "MyOwnDb",
        });

        ImportOutcome outcome = RegistryPortability.Import(destination, RegistryPortability.ReadFile(path), overwriteExisting: true);

        Assert.Equal(2, outcome.Added);
        Assert.Equal(0, outcome.Updated);
        Assert.Equal(2, destination.Targets.Count(t => t.ClientName == "Acme"));
        Assert.Contains(destination.Targets, t => t.ServerName == "my-own-server");
        Assert.Contains(destination.Targets, t => t.ServerName == "sql-01");
    }

    [Fact]
    public void Overwriting_updates_in_place_and_keeps_the_password_already_stored_here()
    {
        ConnectionRegistry source = RegistryWithSecrets();
        string path = Path.Combine(_dir, "share.json");
        RegistryPortability.ExportToFile(source, path);

        ConnectionRegistry destination = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());
        var mine = new DatabaseTarget
        {
            ClientName = "Acme",
            ServerName = "sql-01",
            DatabaseName = "AcmeDb",
            AuthMode = AuthMode.SqlLogin,
            UserId = "someone_else",
            Notes = "mine",
        };
        destination.Upsert(mine, "my-own-local-password");

        ImportOutcome outcome = RegistryPortability.Import(destination, RegistryPortability.ReadFile(path), overwriteExisting: true);

        Assert.Equal(1, outcome.Updated);
        Assert.Single(destination.Targets, t => t.ClientName == "Acme");

        DatabaseTarget updated = destination.Targets.Single(t => t.ClientName == "Acme");
        Assert.Equal("app_user", updated.UserId);   // details came from the file
        Assert.Equal("primary", updated.Notes);
        Assert.Equal("my-own-local-password", destination.ResolvePassword(mine.Id)); // password stayed local
    }

    [Fact]
    public void A_file_that_is_not_ours_is_rejected_clearly()
    {
        string path = Path.Combine(_dir, "junk.json");
        File.WriteAllText(path, "this is not json at all {{{");

        Assert.Throws<InvalidDataException>(() => RegistryPortability.ReadFile(path));
    }

    [Fact]
    public void A_file_from_a_newer_format_is_rejected_rather_than_half_understood()
    {
        string path = Path.Combine(_dir, "future.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { version = 99, databases = Array.Empty<object>() }));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => RegistryPortability.ReadFile(path));
        Assert.Contains("newer version", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Entries_missing_essential_details_are_skipped_not_imported_broken()
    {
        string path = Path.Combine(_dir, "partial.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version = 1,
            databases = new[]
            {
                new { clientName = "Good", serverName = "sql-01", databaseName = "GoodDb" },
                new { clientName = "NoServer", serverName = "", databaseName = "SomeDb" },
            },
        }));

        ConnectionRegistry destination = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());
        ImportOutcome outcome = RegistryPortability.Import(destination, RegistryPortability.ReadFile(path), overwriteExisting: false);

        Assert.Equal(1, outcome.Added);
        Assert.Equal(1, outcome.Skipped);
        Assert.Single(destination.Targets);
    }
}
