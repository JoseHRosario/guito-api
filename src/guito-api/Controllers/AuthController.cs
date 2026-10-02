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
    /// Sign-out token revocation (issue #64) is human-authenticated like the
    /// other routes.
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly ITokenExchangeService _tokenExchangeService;
        private readonly IRevokeGoogleTokenService _revokeService;

        public AuthController(ITokenExchangeService tokenExchangeService, IRevokeGoogleTokenService revokeService)
        {
            _tokenExchangeService = tokenExchangeService;
            _revokeService = revokeService;
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

        [HttpPost("logout")]
        public async Task<IActionResult> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
        {
            try
            {
                await _revokeService.RevokeAsync(request, cancellationToken);
                return NoContent();
            }
            catch (GoogleTokenExchangeException e)
            {
                // RFC 6749 error shape; carries no credentials. Idempotent
                // already-revoked results never throw — success only here.
                return StatusCode((int)e.HttpStatusCode, new GoogleTokenErrorResponse
                {
                    Error = e.Error,
                    ErrorDescription = e.ErrorDescription,
                });
            }
        }
    }
}
