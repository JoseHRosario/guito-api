using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.EnableBanking;
using Microsoft.Extensions.Options;

namespace GuitoApi.Services.BankAuth
{
    /// <summary>
    /// Builds the bank auth redirect URL for the SPA (issue #89): EB POST /auth with the
    /// chosen ASPSP, a fresh random state, and the registered callback URL (config).
    /// The resulting authorization session lives on the EB side; the callback POSTs its
    /// code to /BankAuth/callback which finishes the flow.
    /// </summary>
    public class GetBankAuthUrlService : IGetBankAuthUrlService
    {
        private readonly IEnableBankingClient _client;
        private readonly IOptions<EnableBankingOptions> _options;

        public GetBankAuthUrlService(IEnableBankingClient client, IOptions<EnableBankingOptions> options)
        {
            _client = client;
            _options = options;
        }

        public async Task<BankAuthUrl> GetAsync(string aspspName, string aspspCountry,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_options.Value.AuthCallbackUrl))
                throw new InvalidOperationException(
                    "AppConfiguration:EnableBanking:AuthCallbackUrl is required for the bank auth flow.");
            if (string.IsNullOrWhiteSpace(aspspName) || string.IsNullOrWhiteSpace(aspspCountry))
                throw new ProblemException(400, "Both 'aspsp' and 'country' query parameters are required.");

            EnableBankingStartAuthorization authorization;
            try
            {
                authorization = await _client.StartAuthorizationAsync(
                    aspspName, aspspCountry, Guid.NewGuid().ToString(), _options.Value.AuthCallbackUrl, cancellationToken);
            }
            catch (EnableBankingApiException exception)
            {
                throw BankAuthErrorMapper.ToProblemException(exception);
            }

            return new BankAuthUrl { Url = authorization.Url };
        }
    }
}