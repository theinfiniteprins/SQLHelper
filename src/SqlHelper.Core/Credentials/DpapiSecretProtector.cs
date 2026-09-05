using System.Security.Cryptography;
using System.Text;

namespace SqlHelper.Core.Credentials;

/// <summary>
/// Windows Data Protection API (<see cref="ProtectedData"/>) at
/// <see cref="DataProtectionScope.CurrentUser"/>. Windows derives the key from the logged-in
/// account, so ciphertext cannot be read by another user or on another machine, and there is no
/// key file to manage. Optional entropy (see <see cref="MasterKey"/>) layers a passphrase on top.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private readonly byte[]? _entropy;

    public DpapiSecretProtector(ReadOnlySpan<byte> optionalEntropy = default)
        => _entropy = optionalEntropy.IsEmpty ? null : optionalEntropy.ToArray();

    public byte[] Protect(ReadOnlySpan<char> plaintext)
    {
        byte[] clear = new byte[Encoding.UTF8.GetByteCount(plaintext)];
        try
        {
            Encoding.UTF8.GetBytes(plaintext, clear);
            return ProtectedData.Protect(clear, _entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public string Unprotect(ReadOnlySpan<byte> ciphertext)
    {
        byte[] clear = ProtectedData.Unprotect(ciphertext.ToArray(), _entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public byte[] ProtectBytes(ReadOnlySpan<byte> plaintext)
        => ProtectedData.Protect(plaintext.ToArray(), _entropy, DataProtectionScope.CurrentUser);

    public byte[] UnprotectBytes(ReadOnlySpan<byte> ciphertext)
        => ProtectedData.Unprotect(ciphertext.ToArray(), _entropy, DataProtectionScope.CurrentUser);
}
