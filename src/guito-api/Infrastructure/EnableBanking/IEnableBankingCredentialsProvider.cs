namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>
    /// Where the EB application credentials come from (issue #89). Two sources exist:
    /// the ADR-0008 runtime secrets payload ("Payload") and a dedicated Secrets Manager
    /// secret ("SecretsManager" — chosen for staging so the bank key survives the deploy
    /// script re-seeding the runtime payload).
    /// </summary>
    public interface IEnableBankingCredentialsProvider
    {
        Task<EnableBankingCredentials> GetAsync(CancellationToken cancellationToken = default);
    }
}