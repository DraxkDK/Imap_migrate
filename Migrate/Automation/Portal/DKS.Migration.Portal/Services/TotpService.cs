using System.Security.Cryptography;
using System.Text;

namespace DKS.Migration.Portal.Services;

/// <summary>
/// RFC 6238 TOTP (authenticator-app 2FA): secret generation, the otpauth:// URI for
/// QR enrolment, and 6-digit code verification with a ±1 step tolerance.
/// </summary>
public static class TotpService
{
    private const int Digits = 6;
    private const int PeriodSeconds = 30;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string GenerateSecret(int bytes = 20)
        => Base32Encode(RandomNumberGenerator.GetBytes(bytes));

    public static string GetOtpAuthUri(string issuer, string account, string secret)
    {
        var i = Uri.EscapeDataString(issuer);
        var a = Uri.EscapeDataString(account);
        return $"otpauth://totp/{i}:{a}?secret={secret}&issuer={i}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    /// <summary>Returns true if <paramref name="code"/> is valid for <paramref name="secret"/> now (±window steps).</summary>
    public static bool Verify(string? secret, string? code, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim().Replace(" ", "");
        if (code.Length != Digits) return false;

        var key = Base32Decode(secret);
        if (key.Length == 0) return false;
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / PeriodSeconds;
        for (var w = -window; w <= window; w++)
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(ComputeCode(key, step + w)),
                    Encoding.ASCII.GetBytes(code)))
                return true;
        return false;
    }

    private static string ComputeCode(byte[] key, long step)
    {
        var msg = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(msg);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8) | (hash[offset + 3] & 0xFF);
        var otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString().PadLeft(Digits, '0');
    }

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        input = input.TrimEnd('=').ToUpperInvariant().Replace(" ", "");
        var bytes = new List<byte>();
        int bits = 0, value = 0;
        foreach (var c in input)
        {
            var idx = Base32Alphabet.IndexOf(c);
            if (idx < 0) continue;
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return bytes.ToArray();
    }
}
