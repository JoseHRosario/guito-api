using System.Net.Http.Headers;
using System.Text;

namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>
    /// Real HTTP transport for the Enable Banking API (issue #89): injects a fresh JWT
    /// client assertion (PS256/RS256 per config) into every request's Authorization header.
    /// </summary>
    public class HttpEnableBankingTransport : IEnableBankingTransport
    {
        public const string HttpClientName = "EnableBanking";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEnableBankingJwtSigner _jwtSigner;

        public HttpEnableBankingTransport(IHttpClientFactory httpClientFactory, IEnableBankingJwtSigner jwtSigner)
        {
            _httpClientFactory = httpClientFactory;
            _jwtSigner = jwtSigner;
        }

        public async Task<EnableBankingApiResponse> SendAsync(
            EnableBankingApiRequest request,
            CancellationToken cancellationToken = default)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var message = new HttpRequestMessage(request.Method, request.Path);
            // No `using` on the content: HttpRequestMessage owns and disposes it when the
            // message is disposed — a block-scoped using would dispose it BEFORE SendAsync
            // (hit live: ObjectDisposedException on the first POST /auth).
            if (request.JsonBody is not null)
                message.Content = new StringContent(request.JsonBody, Encoding.UTF8, "application/json");
            message.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", await _jwtSigner.CreateTokenAsync(cancellationToken));

            var response = await client.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new EnableBankingApiResponse((int)response.StatusCode, body);
        }
    }
}