using System.Security.Cryptography;

namespace DKS.Migration.Portal.Services;

/// <summary>
/// Salted PBKDF2-SHA256 password hashing using only the BCL (no extra packages).
/// Stored format: "{base64 salt}:{base64 hash}".
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split(':', 2);
        if (parts.Length != 2) return false;
        try
        {
            byte[] salt = Convert.FromBase64String(parts[0]);
            byte[] expected = Convert.FromBase64String(parts[1]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Basic strength check; returns an error message or null if OK.</summary>
    public static string? ValidateStrength(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return "Password must be at least 8 characters.";
        return null;
    }
}
