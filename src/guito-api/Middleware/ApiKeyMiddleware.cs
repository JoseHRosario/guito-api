using System.Security.Cryptography;
using System.Text;
using GuitoApi.Services;
using Microsoft.Extensions.Options;
using System.Net;
using GuitoApi.Configuration;

namespace GuitoApi.Middleware
{
    /// <summary>
    /// Agent key path (ADR-0003): validates the X-Api-Key header against the
    /// keys stored in the runtime secret. Independent of the Google token path.
    /// </summary>
    public class ApiKeyMiddleware
    {
        public const string ApiKeyHeaderKey = "X-Api-Key";
        private const string PublicPath = "/healthz";

        /// <summary>Public path exempt from the auth gates (owned by ApiKeyMiddleware).</summary>
        public const string PublicPathKey = PublicPath;

        /// <summary>HttpContext.Items marker set after a validated agent key.</summary>
        public const string AgentAuthedKey = "AgentAuthed";

        private readonly RequestDelegate _next;
        private readonly ILogger<ApiKeyMiddleware> _logger;
        private readonly AppConfigurationOptions _options;

        public ApiKeyMiddleware(
            RequestDelegate next,
            ILogger<ApiKeyMiddleware> logger,
            IOptions<AppConfigurationOptions> options)
        {
            _next = next;
            _logger = logger;
            _options = options.Value;
        }

        public async Task InvokeAsync(HttpContext httpContext)
        {
            if (_options.Authentication.ValidateApiKey)
            {
                if (httpContext.Request.Path.StartsWithSegments(PublicPath))
                {
                    await _next(httpContext);
                    return;
                }

                var apiKey = httpContext.Request.Headers[ApiKeyHeaderKey].ToString();
                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogWarning("Missing API key. Returning: {Status}", HttpStatusCode.Unauthorized);
                    await WriteErrorToResponse(httpContext, HttpStatusCode.Unauthorized, "Missing API key");
                    return;
                }

                // Resolve per-request: ISecretsProvider may be scoped (tests) or singleton (prod).
                var secretsProvider = httpContext.RequestServices.GetRequiredService<ISecretsProvider>();
                var payload = await secretsProvider.GetAsync();
                if (!IsKnownKey(payload.ApiKeys, apiKey))
                {
                    _logger.LogWarning("Invalid API key. Returning: {Status}", HttpStatusCode.Unauthorized);
                    await WriteErrorToResponse(httpContext, HttpStatusCode.Unauthorized, "Invalid API key");
                    return;
                }

                // Paths are independent (ADR-0003): a validated agent key must not
                // also have to pass the Google ID token gate downstream.
                httpContext.Items[AgentAuthedKey] = true;
            }
            await _next(httpContext);
        }

        private static bool IsKnownKey(IEnumerable<string> knownKeys, string apiKey)
        {
            foreach (var knownKey in knownKeys)
            {
                if (FixedTimeEquals(knownKey, apiKey))
                    return true;
            }
            return false;
        }

        private static bool FixedTimeEquals(string expected, string actual)
        {
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var actualBytes = Encoding.UTF8.GetBytes(actual);
            // CryptographicOperations.FixedTimeEquals requires equal lengths;
            // length leak is harmless, the branch is on length only.
            return expectedBytes.Length == actualBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        private Task WriteErrorToResponse(HttpContext context, HttpStatusCode statusCode, string errorMessage)
        {
            context.Response.StatusCode = (int)statusCode;
            return context.Response.WriteAsync(errorMessage);
        }
    }
}
