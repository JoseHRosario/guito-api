using GuitoApi.DataTransferObjects.Output;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Services.Auth;

namespace GuitoApi.Tests;

/// <summary>Records token-exchange calls; tests replace ITokenExchangeService with this via DI.</summary>
public class FakeTokenExchangeService : ITokenExchangeService
{
    public List<TokenExchangeRequest> Calls { get; } = [];
    public string? IdToken { get; set; } = "fake-id-token";
    public string? AccessToken { get; set; } = "fake-access-token";
    public long ExpiresIn { get; set; } = 3600;
    /// <summary>When set, the next call throws this instead of returning a token set.</summary>
    public Exception? Throw { get; set; }

    public Task<TokenExchangeResponse> ExchangeAsync(TokenExchangeRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(request);
        if (Throw is not null)
            throw Throw;
        return Task.FromResult(new TokenExchangeResponse
        {
            IdToken = IdToken,
            AccessToken = AccessToken,
            ExpiresIn = ExpiresIn,
        });
    }
}
