using System.Text;
using SqlHelper.Core.Credentials;

namespace SqlHelper.Core.Tests.Fakes;

/// <summary>
/// A reversible XOR transform standing in for <see cref="ISecretProtector"/> in unit tests, so
/// they do not depend on the Windows DPAPI or the identity of the test-runner account.
/// This is NOT encryption and must never be used outside tests.
/// </summary>
public sealed class ReversibleTestProtector : ISecretProtector
{
    private const byte Key = 0x5A;

    public byte[] Protect(ReadOnlySpan<char> plaintext) => Xor(Encoding.UTF8.GetBytes(plaintext.ToArray()));

    public string Unprotect(ReadOnlySpan<byte> ciphertext) => Encoding.UTF8.GetString(Xor(ciphertext.ToArray()));

    public byte[] ProtectBytes(ReadOnlySpan<byte> plaintext) => Xor(plaintext.ToArray());

    public byte[] UnprotectBytes(ReadOnlySpan<byte> ciphertext) => Xor(ciphertext.ToArray());

    private static byte[] Xor(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= Key;
        }

        return bytes;
    }
}
