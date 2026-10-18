namespace GuitoApi.Repositories;

public interface IHumanSessionCredentialProtector
{
    Task<string> EncryptAsync(string credential, string handleHash, CancellationToken cancellationToken = default);
    Task<string> DecryptAsync(string encryptedCredential, string handleHash, CancellationToken cancellationToken = default);
}
