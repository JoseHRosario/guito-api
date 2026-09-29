using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers
{
    /// <summary>
    /// Server-side Google token exchange (issue #52): the UI's PKCE flow cannot
    /// complete against Google directly (client_secret is required and must not
    /// ship in the browser bundle). Unauthenticated by design — the code itself
    /// is the credential; the client_secret stays in the API's secret store.
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly ITokenExchangeService _tokenExchangeService;

        public AuthController(ITokenExchangeService tokenExchangeService)
        {
            _tokenExchangeService = tokenExchangeService;
        }

        [HttpPost("token")]
        public async Task<TokenExchangeResponse> TokenAsync(TokenExchangeRequest request, CancellationToken cancellationToken)
        {
            return await _tokenExchangeService.ExchangeAsync(request, cancellationToken);
        }
    }
}
