using GuitoApi.Configuration;
using GuitoApi.Infrastructure.Secrets;
using Microsoft.Extensions.Options;

namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>Credentials from the runtime secrets payload's enableBanking block (ADR-0008).</summary>
    public class PayloadEnableBankingCredentialsProvider : IEnableBankingCredentialsProvider
    {
        private readonly ISecretsProvider _secretsProvider;

        public PayloadEnableBankingCredentialsProvider(ISecretsProvider secretsProvider)
        {
            _secretsProvider = secretsProvider;
        }

        public async Task<EnableBankingCredentials> GetAsync(CancellationToken cancellationToken = default)
        {
            var payload = (await _secretsProvider.GetAsync(cancellationToken)).EnableBanking;
            return new EnableBankingCredentials(payload.ApplicationId, payload.PrivateKey);
        }
    }
}