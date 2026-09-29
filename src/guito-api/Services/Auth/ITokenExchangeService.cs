using GuitoApi.DataTransferObjects.Output;

using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Input;

namespace GuitoApi.Services.Auth
{
    /// <summary>Server-side Google token exchange (issue #52): PKCE code + verifier + client secret → token set.</summary>
    public interface ITokenExchangeService
    {
        Task<TokenExchangeResponse> ExchangeAsync(TokenExchangeRequest request, CancellationToken cancellationToken = default);
    }
}
