using System.Net;
using System.Security.Claims;
using Google.Apis.Auth;
using GuitoApi.Configuration;
using Microsoft.Extensions.Options;

namespace GuitoApi.Middleware
{
    public class GoogleIdTokenMiddleware
    {
        public const string IdTokenHeaderKey = "x-google-idtoken";

        private readonly RequestDelegate _next;
        private readonly ILogger<GoogleIdTokenMiddleware> _logger;
        private readonly AppConfigurationOptions _options;

        public GoogleIdTokenMiddleware(
            RequestDelegate next,
            ILogger<GoogleIdTokenMiddleware> logger,
            IOptions<AppConfigurationOptions> options)
        {
            _next = next;
            _logger = logger;
            _options = options.Value;
        }

        public async Task InvokeAsync(HttpContext httpContext)
        {
            if (!_options.Authentication.ValidateIdToken)
            {
                await _next(httpContext);
                return;
            }

            // /healthz stays public (single definition in ApiKeyMiddleware); a request that
            // already passed the agent key gate does not need a Google token (paths
            // stay independent — ADR-0003: this only skips the token check).
            if (httpContext.Request.Path.StartsWithSegments(ApiKeyMiddleware.PublicPathKey) ||
                httpContext.Items.ContainsKey(ApiKeyMiddleware.AgentAuthedKey))
            {
                await _next(httpContext);
                return;
            }

            try
            {
                var payload = await ValidateTokenAsync(httpContext);
                if (payload is null)
                    return; // response already written

                var identity = new ClaimsIdentity("Google");
                identity.AddClaim(new Claim(ClaimTypes.Email, payload.Email));
                httpContext.User = new ClaimsPrincipal(identity);
            }
            catch (InvalidJwtException e)
            {
                _logger.LogWarning("IdentityToken failed validation, {Message}", e.Message);
                await AuthErrorResponseWriter.WriteAsync(httpContext, HttpStatusCode.Forbidden, e.Message);
                return;
            }
            catch (Exception)
            {
                _logger.LogWarning("IdentityToken failed validation");
                await AuthErrorResponseWriter.WriteAsync(httpContext, HttpStatusCode.Forbidden, "Invalid IdentityToken");
                return;
            }

            await _next(httpContext);
        }

        private async Task<GoogleJsonWebSignature.Payload?> ValidateTokenAsync(HttpContext httpContext)
        {
            var identityToken = httpContext.Request.Headers[IdTokenHeaderKey];

            if (string.IsNullOrEmpty(identityToken))
            {
                _logger.LogWarning("Missing identityToken from http header. Returning: {Status}", HttpStatusCode.Unauthorized);
                await AuthErrorResponseWriter.WriteAsync(httpContext, HttpStatusCode.Unauthorized, "Missing IdentityToken");
                return null;
            }

            var payload = await GoogleJsonWebSignature.ValidateAsync(identityToken);
            if (!IsValidToken(payload))
            {
                _logger.LogWarning("IdentityToken failed validation rules for email: {email}", payload.Email);
                await AuthErrorResponseWriter.WriteAsync(httpContext, HttpStatusCode.Forbidden, "Invalid IdentityToken");
                return null;
            }

            return payload;
        }

        private bool IsValidToken(GoogleJsonWebSignature.Payload payload)
        {
            _logger.LogInformation("Validating token for email: {email}", payload.Email);
            return _options.Authentication.AllowedLogins.Contains(payload.Email) &&
                    _options.Authentication.OAuthAudience == payload.Audience.ToString();
        }
    }
}