using System.Security.Cryptography;

namespace SqlHelper.Core.Credentials;

/// <summary>
/// Derives DPAPI entropy from an optional operator passphrase. When a passphrase is set, the
/// registry cannot be decrypted even by the same Windows account without it. The fixed salt is
/// acceptable here because the derived bytes are only ever <em>additional</em> entropy on top of
/// DPAPI's per-user key — never the sole protection.
/// </summary>
public static class MasterKey
{
    private const int Iterations = 600_000;

    private static readonly byte[] Salt = "SqlHelper/registry-entropy/v1"u8.ToArray();

    /// <summary>Returns 32 bytes of entropy, or an empty span when no passphrase is supplied.</summary>
    public static byte[] DeriveEntropy(ReadOnlySpan<char> passphrase)
    {
        if (passphrase.IsEmpty)
        {
            return [];
        }

        return Rfc2898DeriveBytes.Pbkdf2(passphrase, Salt, Iterations, HashAlgorithmName.SHA256, outputLength: 32);
    }
}
