namespace PstMigration.Application.Abstractions;

/// <summary>Encrypts/decrypts secrets at rest (e.g. Entra client secrets).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}
