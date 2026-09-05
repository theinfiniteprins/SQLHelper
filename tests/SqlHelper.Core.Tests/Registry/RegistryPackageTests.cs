using System.Text;
using System.Text.Json;
using SqlHelper.Core.Model;
using SqlHelper.Core.Registry;
using SqlHelper.Core.Tests.Fakes;

namespace SqlHelper.Core.Tests.Registry;

/// <summary>
/// The encrypted export carries passwords, so these tests are as much about what must NOT be
/// possible as what must.
/// </summary>
public sealed class RegistryPackageTests : IDisposable
{
    private const string SecretPassword = "Sup3rSecret!Passw0rd";
    private const string Passphrase = "correct horse battery staple";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlhelper-package", Guid.NewGuid().ToString("N"));

    public RegistryPackageTests() => Directory.CreateDirectory(_dir);

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

    private static ConnectionRegistry SourceRegistry()
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
            },
            SecretPassword);

        registry.Upsert(new DatabaseTarget
        {
            ClientName = "Globex",
            ServerName = "sql-02",
            DatabaseName = "GlobexDb",
            AuthMode = AuthMode.WindowsIntegrated,
        });

        return registry;
    }

    private string Export(ConnectionRegistry registry, string passphrase = Passphrase, string name = "share.sqlhx")
    {
        string path = Path.Combine(_dir, name);
        RegistryPackage.ExportEncrypted(registry, path, passphrase, exportedBy: "tester");
        return path;
    }

    // ---------------------------------------------------------------- security

    [Fact]
    public void The_password_never_appears_in_the_exported_file()
    {
        string path = Export(SourceRegistry());

        string asText = File.ReadAllText(path);
        Assert.DoesNotContain(SecretPassword, asText, StringComparison.Ordinal);

        // Not merely absent as text: absent from the raw bytes too.
        byte[] raw = File.ReadAllBytes(path);
        byte[] needle = Encoding.UTF8.GetBytes(SecretPassword);
        Assert.False(Contains(raw, needle), "the password appeared in the file's bytes");
    }

    [Fact]
    public void Connection_details_are_not_readable_without_the_passphrase()
    {
        string path = Export(SourceRegistry());
        string asText = File.ReadAllText(path);

        Assert.DoesNotContain("sql-01", asText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AcmeDb", asText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("app_user", asText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_wrong_passphrase_is_refused()
    {
        string path = Export(SourceRegistry());

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => RegistryPackage.ImportEncrypted(path, "not the right passphrase"));

        Assert.Contains("passphrase is wrong", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_file_altered_after_export_is_refused_rather_than_partly_trusted()
    {
        string path = Export(SourceRegistry());

        // Flip one byte of the ciphertext, as a corrupted copy or a meddling hand would.
        var package = JsonSerializer.Deserialize<EncryptedPackage>(File.ReadAllText(path))!;
        byte[] payload = Convert.FromBase64String(package.Payload);
        payload[payload.Length / 2] ^= 0xFF;
        File.WriteAllText(path, JsonSerializer.Serialize(package with { Payload = Convert.ToBase64String(payload) }));

        Assert.Throws<InvalidDataException>(() => RegistryPackage.ImportEncrypted(path, Passphrase));
    }

    [Fact]
    public void Two_exports_of_the_same_data_produce_different_ciphertext()
    {
        // A fresh salt and nonce every time, so identical registries do not produce identical
        // files that could be compared against each other.
        ConnectionRegistry registry = SourceRegistry();
        string first = File.ReadAllText(Export(registry, name: "a.sqlhx"));
        string second = File.ReadAllText(Export(registry, name: "b.sqlhx"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_passphrase_that_is_too_short_is_refused_at_export()
    {
        Assert.Throws<ArgumentException>(
            () => RegistryPackage.ExportEncrypted(SourceRegistry(), Path.Combine(_dir, "x.sqlhx"), "short"));
    }

    [Fact]
    public void The_key_derivation_is_strong_enough_to_be_worth_having()
    {
        string path = Export(SourceRegistry());
        var package = JsonSerializer.Deserialize<EncryptedPackage>(File.ReadAllText(path))!;

        Assert.Equal("PBKDF2-SHA256", package.Kdf);
        Assert.True(package.Iterations >= 600_000, $"only {package.Iterations} iterations");
    }

    [Fact]
    public void A_file_claiming_a_trivially_weak_iteration_count_is_still_opened_at_a_strong_one()
    {
        // Someone editing the header down to 1 iteration must not make the passphrase cheap to
        // attack, nor make the file open with the wrong key.
        string path = Export(SourceRegistry());
        var package = JsonSerializer.Deserialize<EncryptedPackage>(File.ReadAllText(path))!;
        File.WriteAllText(path, JsonSerializer.Serialize(package with { Iterations = 1 }));

        Assert.Throws<InvalidDataException>(() => RegistryPackage.ImportEncrypted(path, Passphrase));
    }

    // ---------------------------------------------------------------- behaviour

    [Fact]
    public void Export_then_import_carries_everything_including_passwords()
    {
        string path = Export(SourceRegistry());

        ConnectionRegistry destination = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());
        PortableBundle bundle = RegistryPackage.ImportEncrypted(path, Passphrase);
        ImportOutcome outcome = RegistryPackage.Import(destination, bundle, overwriteExisting: false);

        Assert.Equal(2, outcome.Added);

        DatabaseTarget acme = destination.Targets.Single(t => t.ClientName == "Acme");
        Assert.Equal("sql-01", acme.ServerName);
        Assert.Equal("AcmeDb", acme.DatabaseName);
        Assert.Equal("app_user", acme.UserId);
        Assert.True(acme.TrustServerCertificate);
        Assert.Equal(ServerEnvironment.Production, acme.Environment);

        // The point of the whole exercise: it is ready to use with no further typing.
        Assert.True(destination.HasPassword(acme.Id));
        Assert.Equal(SecretPassword, destination.ResolvePassword(acme.Id));
    }

    [Fact]
    public void The_imported_password_is_re_encrypted_for_the_machine_that_received_it()
    {
        string path = Export(SourceRegistry());

        // A different protector stands in for a different Windows account on another PC.
        ConnectionRegistry destination = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());
        RegistryPackage.Import(destination, RegistryPackage.ImportEncrypted(path, Passphrase), overwriteExisting: false);

        DatabaseTarget acme = destination.Targets.Single(t => t.ClientName == "Acme");
        Assert.Equal(SecretPassword, destination.ResolvePassword(acme.Id));
    }

    [Fact]
    public void Importing_says_how_many_passwords_came_across()
    {
        string path = Export(SourceRegistry());
        ConnectionRegistry destination = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());

        ImportOutcome outcome = RegistryPackage.Import(
            destination, RegistryPackage.ImportEncrypted(path, Passphrase), overwriteExisting: false);

        Assert.Contains(outcome.Notes, n => n.Contains("password(s) came across", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(outcome.Notes, n => n.Contains("set its password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_encrypted_package_is_told_apart_from_a_plain_list()
    {
        string encrypted = Export(SourceRegistry());
        string plain = Path.Combine(_dir, "plain.json");
        RegistryPortability.ExportToFile(SourceRegistry(), plain);

        Assert.True(RegistryPackage.IsEncryptedPackage(encrypted));
        Assert.False(RegistryPackage.IsEncryptedPackage(plain));
    }

    // ------------------------------------------- one client, several databases

    [Fact]
    public void A_client_with_two_databases_keeps_each_ones_own_password()
    {
        ConnectionRegistry source = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());
        source.Upsert(
            new DatabaseTarget
            {
                ClientName = "Acme", ServerName = "sql-01", DatabaseName = "AcmeApp",
                AuthMode = AuthMode.SqlLogin, UserId = "app_user",
            },
            "app-password");
        source.Upsert(
            new DatabaseTarget
            {
                ClientName = "Acme", ServerName = "sql-01", DatabaseName = "AcmeReporting",
                AuthMode = AuthMode.SqlLogin, UserId = "report_user",
            },
            "reporting-password");

        string path = Export(source, name: "two-dbs.sqlhx");
        PortableBundle bundle = RegistryPackage.ImportEncrypted(path, Passphrase);

        ConnectionRegistry destination = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());
        ImportOutcome outcome = RegistryPackage.Import(destination, bundle, overwriteExisting: true);

        // Both databases arrive as separate entries — not merged into one by their shared name.
        Assert.Equal(2, outcome.Added);
        Assert.Equal(2, destination.Targets.Count);

        DatabaseTarget app = destination.Targets.Single(t => t.DatabaseName == "AcmeApp");
        DatabaseTarget reporting = destination.Targets.Single(t => t.DatabaseName == "AcmeReporting");

        // And each keeps its own credential rather than inheriting the other's.
        Assert.Equal("app-password", destination.ResolvePassword(app.Id));
        Assert.Equal("reporting-password", destination.ResolvePassword(reporting.Id));
    }

    [Fact]
    public void Re_importing_the_same_file_updates_in_place_instead_of_duplicating()
    {
        string path = Export(SourceRegistry());
        ConnectionRegistry destination = ConnectionRegistry.CreateEmpty(new ReversibleTestProtector());

        RegistryPackage.Import(destination, RegistryPackage.ImportEncrypted(path, Passphrase), overwriteExisting: true);
        int afterFirst = destination.Targets.Count;
        RegistryPackage.Import(destination, RegistryPackage.ImportEncrypted(path, Passphrase), overwriteExisting: true);

        Assert.Equal(afterFirst, destination.Targets.Count);
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
