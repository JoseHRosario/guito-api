using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
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
        public async Task<IActionResult> TokenAsync(TokenExchangeRequest request, CancellationToken cancellationToken)
        {
            try
            {
                return Ok(await _tokenExchangeService.ExchangeAsync(request, cancellationToken));
            }
            catch (GoogleTokenExchangeException e)
            {
                // RFC 6749 error shape (guito-ui tokenFailureMessage parses it); carries no credentials.
                return StatusCode((int)e.HttpStatusCode, new GoogleTokenErrorResponse
                {
                    Error = e.Error,
                    ErrorDescription = e.ErrorDescription,
                });
            }
        }
    }
}
