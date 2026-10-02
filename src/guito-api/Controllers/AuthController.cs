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
            return await CallGoogleAsync(
                () => _tokenExchangeService.ExchangeAsync(request, cancellationToken), Ok);
        }

        [HttpPost("logout")]
        public async Task<IActionResult> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
        {
            // Idempotent already-revoked results never throw — success only here.
            return await CallGoogleAsync(
                () => _revokeService.RevokeAsync(request, cancellationToken), NoContent);
        }

        /// <summary>
        /// Shared Google error mapping for every Auth action: Google's RFC 6749
        /// error/error_description surface verbatim (guito-ui tokenFailureMessage
        /// parses this shape, NOT ProblemDetails) — a deliberate exception to the
        /// controllers-never-build-responses rule, documented in AGENTS.md.
        /// Carries no credentials.
        /// </summary>
        private async Task<IActionResult> CallGoogleAsync(
            Func<Task> action, Func<IActionResult> success)
        {
            try
            {
                await action();
                return success();
            }
            catch (GoogleTokenExchangeException e)
            {
                return GoogleError(e);
            }
        }

        private async Task<IActionResult> CallGoogleAsync<T>(
            Func<Task<T>> action, Func<T, IActionResult> success)
        {
            try
            {
                return success(await action());
            }
            catch (GoogleTokenExchangeException e)
            {
                return GoogleError(e);
            }
        }

        private IActionResult GoogleError(GoogleTokenExchangeException e) =>
            StatusCode((int)e.HttpStatusCode, new GoogleTokenErrorResponse
            {
                Error = e.Error,
                ErrorDescription = e.ErrorDescription,
            });
    }
}
