using System.Security.Cryptography;
using System.Text;

namespace DKS.Migration.Portal.Services;

/// <summary>
/// AES-GCM encryption for secrets at rest (Entra client secrets). Master key from
/// config "M365:MasterKey" (base64 of 32 bytes). Generate:
///   $b=New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
/// </summary>
public sealed class SecretProtector
{
    private readonly byte[] _key;

    public SecretProtector(IConfiguration config)
    {
        var b64 = config["M365:MasterKey"];
        if (string.IsNullOrWhiteSpace(b64))
            throw new InvalidOperationException("M365:MasterKey is not configured (base64 of 32 bytes).");
        _key = Convert.FromBase64String(b64);
        if (_key.Length != 32)
            throw new InvalidOperationException("M365:MasterKey must decode to exactly 32 bytes.");
    }

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        var combined = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, combined, nonce.Length + tag.Length, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    public string Unprotect(string ciphertext)
    {
        var combined = Convert.FromBase64String(ciphertext);
        var nonceLen = AesGcm.NonceByteSizes.MaxSize;
        var tagLen = AesGcm.TagByteSizes.MaxSize;
        var nonce = combined.AsSpan(0, nonceLen);
        var tag = combined.AsSpan(nonceLen, tagLen);
        var cipher = combined.AsSpan(nonceLen + tagLen);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, tagLen);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
