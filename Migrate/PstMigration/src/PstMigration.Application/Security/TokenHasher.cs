using System.Security.Cryptography;
using System.Text;

namespace PstMigration.Application.Security;

/// <summary>Hashes registration tokens so the raw token is never stored.</summary>
public static class TokenHasher
{
    public static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
