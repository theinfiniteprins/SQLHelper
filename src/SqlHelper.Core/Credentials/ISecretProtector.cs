namespace SqlHelper.Core.Credentials;

/// <summary>
/// Encrypts and decrypts data for storage at rest — SQL login passwords, and the registry file
/// as a whole. Implementations must bind ciphertext to the current OS user and machine, so that a
/// copied file is useless on any other account or computer.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypt <paramref name="plaintext"/> characters. The caller should clear the span afterwards.</summary>
    byte[] Protect(ReadOnlySpan<char> plaintext);

    /// <summary>Decrypt a blob from <see cref="Protect(ReadOnlySpan{char})"/> back to a string.</summary>
    string Unprotect(ReadOnlySpan<byte> ciphertext);

    /// <summary>Encrypt arbitrary bytes (used for the whole registry envelope).</summary>
    byte[] ProtectBytes(ReadOnlySpan<byte> plaintext);

    /// <summary>Decrypt a blob from <see cref="ProtectBytes"/>.</summary>
    byte[] UnprotectBytes(ReadOnlySpan<byte> ciphertext);
}
