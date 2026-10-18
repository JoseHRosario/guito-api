using System.Security.Cryptography;
using System.Text;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.Secrets;
using GuitoApi.Repositories;

namespace GuitoApi.Infrastructure.Auth;

public sealed class AesGcmUserSessionCredentialProtector(ISecretsProvider secrets) : IUserSessionCredentialProtector
{
    public async Task<string> EncryptAsync(string credential, string handleHash, CancellationToken cancellationToken = default)
    {
        var key = await ReadKeyAsync(cancellationToken);
        try { return Encrypt(credential, handleHash, key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static string Encrypt(string credential, string handleHash, byte[] key)
    {
        var plaintext = Encoding.UTF8.GetBytes(credential);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var cipher = new AesGcm(key, 16);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(handleHash));
            return $"v1.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(tag)}.{Convert.ToBase64String(ciphertext)}";
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task<string> DecryptAsync(string encryptedCredential, string handleHash, CancellationToken cancellationToken = default)
    {
        var key = await ReadKeyAsync(cancellationToken);
        try { return Decrypt(encryptedCredential, handleHash, key); }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        { throw UserSessionException.Unavailable(); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static string Decrypt(string encryptedCredential, string handleHash, byte[] key)
    {
        var parts = encryptedCredential.Split('.');
        if (parts.Length != 4 || parts[0] != "v1") throw UserSessionException.Unavailable();
        var ciphertext = Convert.FromBase64String(parts[3]);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(Convert.FromBase64String(parts[1]), ciphertext, Convert.FromBase64String(parts[2]), plaintext, Encoding.UTF8.GetBytes(handleHash));
            return Encoding.UTF8.GetString(plaintext);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task<byte[]> ReadKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var payload = await secrets.GetAsync(cancellationToken);
            var key = DecodeKey(payload.HasUserSessionEncryptionKey ? payload.UserSessionEncryptionKey : payload.HumanSessionEncryptionKey);
            try
            {
                if (payload.HasUserSessionEncryptionKey && payload.HasLegacyUserSessionEncryptionKey)
                    ValidateLegacyAlias(key, payload.HumanSessionEncryptionKey);
                return key;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }
        catch (FormatException) { throw UserSessionException.Unavailable(); }
    }

    private static byte[] DecodeKey(string? encoded)
    {
        if (encoded is null) throw new FormatException();
        var key = Convert.FromBase64String(encoded);
        if (key.Length == 32 && Convert.ToBase64String(key) == encoded) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new FormatException();
    }

    private static void ValidateLegacyAlias(byte[] key, string? encoded)
    {
        var legacy = DecodeKey(encoded);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(key, legacy)) throw new FormatException();
        }
        finally { CryptographicOperations.ZeroMemory(legacy); }
    }
}
