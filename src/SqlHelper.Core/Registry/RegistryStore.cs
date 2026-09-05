using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlHelper.Core.Credentials;
using SqlHelper.Core.Serialization;

namespace SqlHelper.Core.Registry;

/// <summary>
/// Loads and saves the registry as a single file that is JSON-serialized and then encrypted in
/// full with <see cref="ISecretProtector"/>. Whole-file encryption hides server names and
/// structure at rest; individual passwords are <em>also</em> encrypted inside the JSON so they
/// stay protected in memory until a connection is opened.
/// </summary>
public sealed class RegistryStore
{
    // Marks a decrypted payload as ours before we try to parse it — a clearer error than a JSON exception.
    private static readonly byte[] Magic = [0x53, 0x51, 0x4C, 0x48, 0x4C, 0x50, 0x52, 0x01]; // "SQLHLPR" + 0x01

    private readonly string _filePath;
    private readonly ISecretProtector _protector;

    public RegistryStore(string filePath, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public string FilePath => _filePath;

    public bool Exists => File.Exists(_filePath);

    /// <summary>Returns an empty registry when the file does not exist yet.</summary>
    public ConnectionRegistry Load()
    {
        if (!File.Exists(_filePath))
        {
            return ConnectionRegistry.CreateEmpty(_protector);
        }

        byte[] envelope = File.ReadAllBytes(_filePath);
        byte[] plain;
        try
        {
            plain = _protector.UnprotectBytes(envelope);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException(
                "The registry file could not be decrypted on this account/machine. " +
                "It may belong to another Windows user, or a master passphrase is required.", ex);
        }

        try
        {
            if (plain.Length < Magic.Length || !plain.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                throw new InvalidDataException("Registry file did not have the expected header after decryption.");
            }

            string json = Encoding.UTF8.GetString(plain.AsSpan(Magic.Length));
            RegistryDocument doc = JsonSerializer.Deserialize<RegistryDocument>(json, Json.Options)
                ?? throw new InvalidDataException("Registry file decrypted but contained no document.");

            if (doc.SchemaVersion > RegistryDocument.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Registry schema version {doc.SchemaVersion} is newer than this build supports " +
                    $"({RegistryDocument.CurrentSchemaVersion}).");
            }

            return new ConnectionRegistry(doc, _protector);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Save(ConnectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        string json = JsonSerializer.Serialize(registry.Document, Json.Options);
        byte[] plain = new byte[Magic.Length + Encoding.UTF8.GetByteCount(json)];
        byte[] envelope;
        try
        {
            Magic.CopyTo(plain, 0);
            Encoding.UTF8.GetBytes(json, plain.AsSpan(Magic.Length));
            envelope = _protector.ProtectBytes(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        // Atomic replace: write a sibling temp file, then move over the target.
        string temp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, envelope);
        try
        {
            File.Move(temp, _filePath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
