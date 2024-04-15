using Google.Apis.Auth;
using Microsoft.Extensions.Options;
using System.Net;
using GuitoApi.Configuration;
using System.Security.Claims;

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
            if (_options.Authentication.ValidateIdToken)
            {
                var identityToken = string.Empty;
                try
                {
                    identityToken = httpContext.Request.Headers[IdTokenHeaderKey];

                    if (string.IsNullOrEmpty(identityToken))
                    {
                        _logger.LogWarning($"Missing identityToken from http header. Returning:  {HttpStatusCode.Unauthorized}");
                        await WriteErrorToResponse(httpContext, HttpStatusCode.Unauthorized, "Missing IdentityToken");
                        return;
                    }

                    GoogleJsonWebSignature.Payload payload = await GoogleJsonWebSignature.ValidateAsync(identityToken);
                    if (!IsValidToken(payload))
                    {
                        _logger.LogWarning("IdentityToken failed validation rules: {identityToken} ", identityToken);
                        await WriteErrorToResponse(httpContext, HttpStatusCode.Unauthorized, "Invalid IdentityToken");
                        return;
                    }

                    var identity = new ClaimsIdentity("Google");
                    identity.AddClaim(new Claim(ClaimTypes.Email, payload.Email));
                    httpContext.User = new ClaimsPrincipal(identity);
                }
                catch (InvalidJwtException e)
                {
                    _logger.LogWarning("IdentityToken failed validation, {Message}: {identityToken} ", e.Message, identityToken);
                    await WriteErrorToResponse(httpContext, HttpStatusCode.Unauthorized, e.Message);
                    return;
                }
                catch (Exception)
                {
                    _logger.LogWarning("IdentityToken failed validation: {identityToken} ", identityToken);
                    await WriteErrorToResponse(httpContext, HttpStatusCode.Unauthorized, "Invalid IdentityToken");
                    return;
                }
            }
            await _next(httpContext);
        }

        private bool IsValidToken(GoogleJsonWebSignature.Payload payload)
        {
            _logger.LogInformation("Validating token for email: {email}", payload.Email);
            return _options.Authentication.AllowedLogins.Contains(payload.Email) &&
                    _options.Authentication.OAuthAudience == payload.Audience.ToString();
        }
        private Task WriteErrorToResponse(HttpContext context, HttpStatusCode statusCode, string errorMessage)
        {
            context.Response.StatusCode = (int)statusCode;
            return context.Response.WriteAsync(errorMessage);
        }
    }
}