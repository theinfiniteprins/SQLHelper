using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlHelper.Core.Serialization;

namespace SqlHelper.Core.Registry;

/// <summary>Everything needed to recreate a registry elsewhere, passwords included. Only ever exists encrypted on disk.</summary>
public sealed record PortableBundle
{
    public required PortableRegistry Registry { get; init; }

    /// <summary>
    /// Password per database, keyed by <see cref="RegistryPortability.IdentityKey(PortableTarget)"/> —
    /// client, server and database together, so a client with more than one database never gets
    /// another one's credentials. Present only inside the encrypted payload.
    /// </summary>
    public Dictionary<string, string> Passwords { get; init; } = [];
}

/// <summary>The on-disk shape of an encrypted export. The payload is opaque without the passphrase.</summary>
public sealed record EncryptedPackage
{
    public const int CurrentVersion = 1;

    public int PackageVersion { get; init; } = CurrentVersion;

    public string Note { get; init; } =
        "SqlHelper database list. Encrypted with AES-256-GCM; a passphrase is required to open it.";

    public string Kdf { get; init; } = "PBKDF2-SHA256";

    public int Iterations { get; init; } = RegistryPackage.Pbkdf2Iterations;

    public required string Salt { get; init; }

    public required string Nonce { get; init; }

    public required string Tag { get; init; }

    public required string Payload { get; init; }
}

/// <summary>
/// Moves a whole registry to another machine — servers, databases, logins <em>and</em> passwords —
/// as one file the recipient can simply import.
///
/// Because it carries credentials, the file is never written in the clear. The payload is
/// encrypted with AES-256-GCM under a key derived from a passphrase you choose (PBKDF2-SHA256,
/// <see cref="Pbkdf2Iterations"/> iterations, fresh random salt and nonce each time). GCM is
/// authenticated, so a file that has been altered by so much as one byte fails to open rather
/// than yielding altered connection details. Tell the recipient the passphrase by some other
/// route than the file itself, and the file is safe to send however you like.
///
/// Nothing here contacts anything: this is a local file written to a path you pick.
/// </summary>
public static class RegistryPackage
{
    public const int Pbkdf2Iterations = 600_000;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;   // AES-GCM standard nonce length
    private const int TagBytes = 16;     // AES-GCM full-strength tag
    private const int KeyBytes = 32;     // AES-256

    /// <summary>Writes the whole registry, passwords included, encrypted under <paramref name="passphrase"/>.</summary>
    public static void ExportEncrypted(
        ConnectionRegistry registry,
        string path,
        ReadOnlySpan<char> passphrase,
        string? exportedBy = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (passphrase.IsWhiteSpace() || passphrase.Length < MinimumPassphraseLength)
        {
            throw new ArgumentException(
                $"Choose a passphrase of at least {MinimumPassphraseLength} characters — it is the only thing protecting the passwords in this file.",
                nameof(passphrase));
        }

        var bundle = new PortableBundle
        {
            Registry = RegistryPortability.BuildExport(registry, exportedBy),
        };

        foreach (Model.DatabaseTarget target in registry.Targets)
        {
            if (target.AuthMode == Model.AuthMode.SqlLogin && registry.HasPassword(target.Id))
            {
                bundle.Passwords[RegistryPortability.IdentityKey(target)] = registry.ResolvePassword(target.Id);
            }
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bundle, Json.Compact));
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] key = DeriveKey(passphrase, salt);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagBytes];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            var package = new EncryptedPackage
            {
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Payload = Convert.ToBase64String(ciphertext),
            };

            File.WriteAllText(path, JsonSerializer.Serialize(package, Json.Options), new UTF8Encoding(false));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Shortest passphrase accepted on export. Short enough to type, long enough to matter.</summary>
    public const int MinimumPassphraseLength = 8;

    /// <summary>True when the file at this path is an encrypted package rather than a plain list.</summary>
    public static bool IsEncryptedPackage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Property names are compared without regard to case: the file may have been written
            // by a build using different naming, and guessing wrong would send an encrypted file
            // down the plain-text path.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                names.Add(property.Name);
            }

            return names.Contains("payload") && names.Contains("salt") && names.Contains("nonce");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Opens an encrypted package. Throws <see cref="InvalidDataException"/> for a wrong passphrase or an altered file.</summary>
    public static PortableBundle ImportEncrypted(string path, ReadOnlySpan<char> passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        EncryptedPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<EncryptedPackage>(File.ReadAllText(path), Json.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"That file is not a SqlHelper export: {ex.Message}", ex);
        }

        if (package is null)
        {
            throw new InvalidDataException("That file is empty.");
        }

        if (package.PackageVersion > EncryptedPackage.CurrentVersion)
        {
            throw new InvalidDataException(
                $"That file was written by a newer version of SqlHelper (format {package.PackageVersion}).");
        }

        byte[] salt = DecodeOrThrow(package.Salt, nameof(package.Salt));
        byte[] nonce = DecodeOrThrow(package.Nonce, nameof(package.Nonce));
        byte[] tag = DecodeOrThrow(package.Tag, nameof(package.Tag));
        byte[] ciphertext = DecodeOrThrow(package.Payload, nameof(package.Payload));

        // Honour the file's own iteration count so older exports keep opening, but never accept a
        // number low enough to make brute-forcing the passphrase cheap.
        int iterations = Math.Max(package.Iterations, 100_000);

        byte[] key = DeriveKey(passphrase, salt, iterations);
        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            // GCM cannot tell "wrong key" from "tampered"; both mean the same thing here.
            throw new InvalidDataException(
                "Could not open that file. Either the passphrase is wrong, or the file has been altered since it was exported.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        try
        {
            PortableBundle? bundle = JsonSerializer.Deserialize<PortableBundle>(
                Encoding.UTF8.GetString(plaintext), Json.Compact);

            return bundle ?? throw new InvalidDataException("That file decrypted but held nothing.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"That file decrypted but its contents are not readable: {ex.Message}", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Applies a decrypted bundle, restoring passwords along with everything else.</summary>
    public static ImportOutcome Import(ConnectionRegistry registry, PortableBundle bundle, bool overwriteExisting)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bundle);

        ImportOutcome outcome = RegistryPortability.Import(registry, bundle.Registry, overwriteExisting);

        var notes = outcome.Notes.ToList();
        int restored = 0;

        foreach ((string identity, string password) in bundle.Passwords)
        {
            Model.DatabaseTarget? target = registry.Targets.FirstOrDefault(
                t => RegistryPortability.IdentityKey(t) == identity);

            if (target is null || target.AuthMode != Model.AuthMode.SqlLogin)
            {
                continue;
            }

            registry.SetPassword(target.Id, password);
            restored++;
        }

        if (restored > 0)
        {
            // The "set a password" warnings from the plain import no longer apply to these.
            notes.RemoveAll(n => n.Contains("set its password", StringComparison.OrdinalIgnoreCase));
            notes.Insert(0, $"{restored} password(s) came across with the file — those databases are ready to use.");
        }

        return outcome with { Notes = notes };
    }

    private static byte[] DeriveKey(ReadOnlySpan<char> passphrase, byte[] salt, int iterations = Pbkdf2Iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

    private static byte[] DecodeOrThrow(string value, string field)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"That file is damaged: the {field.ToLowerInvariant()} is not readable.", ex);
        }
    }
}
