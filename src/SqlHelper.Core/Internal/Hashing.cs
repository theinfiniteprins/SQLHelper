using System.Security.Cryptography;
using System.Text;

namespace SqlHelper.Core.Internal;

internal static class Hashing
{
    public static string Sha256Hex(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Short, human-comparable prefix of a hash for display ("a1b2c3d4").</summary>
    public static string Short(string hexHash) => hexHash.Length <= 8 ? hexHash : hexHash[..8];
}
