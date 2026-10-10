namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>Bank credentials from the runtime payload or a dedicated SSM SecureString.</summary>
    public interface IEnableBankingCredentialsProvider
    {
        Task<EnableBankingCredentials> GetAsync(CancellationToken cancellationToken = default);
    }
}