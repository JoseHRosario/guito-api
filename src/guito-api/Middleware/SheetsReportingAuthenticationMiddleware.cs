using System.Net;
using System.Text.Json;
using GuitoApi.Configuration;
using GuitoApi.Infrastructure.Reporting;
using Microsoft.Extensions.Options;

namespace GuitoApi.Middleware;

public class SheetsReportingAuthenticationMiddleware(RequestDelegate next, IOptions<AppConfigurationOptions> options, IJwksClient jwksClient)
{
    public const string AuthedKey = "SheetsReportingAuthed";

    public async Task InvokeAsync(HttpContext context)
    {
        var token = context.Request.Headers[GoogleIdTokenMiddleware.IdTokenHeaderKey].ToString();
        var reporting = options.Value.SheetsReportingAuthentication;
        var authorization = context.Request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : string.Empty;
        if (!HasAudience(token, reporting.Audience) && !HasAudience(bearer, reporting.Audience))
        {
            await next(context);
            return;
        }
        if (!await IsValidReportingRequestAsync(context, token, bearer, reporting))
        {
            await AuthErrorResponseWriter.WriteAsync(context, HttpStatusCode.Forbidden, "Invalid reporting identity");
            return;
        }
        context.Items[AuthedKey] = true;
        await next(context);
    }

    private async Task<bool> IsValidReportingRequestAsync(HttpContext context, string token, string bearer,
        SheetsReportingAuthentication reporting)
    {
        if (!IsAllowedRequest(context, token, bearer, reporting)) return false;
        try
        {
            return (await new GoogleTokenValidator(jwksClient, reporting.Audience, reporting.Subject)
                .ValidateAsync(token, context.RequestAborted)).Valid;
        }
        catch (Exception) { return false; }
    }

    private bool IsAllowedRequest(HttpContext context, string token, string bearer, SheetsReportingAuthentication reporting) =>
        !string.IsNullOrWhiteSpace(reporting.Subject) && reporting.Audience != options.Value.Authentication.OAuthAudience
        && context.Request.Method == "GET" && string.Equals(context.Request.Path.Value, "/Expense/history", StringComparison.OrdinalIgnoreCase)
        && token.Length > 0 && token == bearer;

    // Untrusted audience is a dispatch hint only; never authentication evidence.
    private static bool HasAudience(string token, string audience)
    {
        if (string.IsNullOrWhiteSpace(audience)) return false;
        try
        {
            var part = token.Split('.')[1];
            using var payload = JsonDocument.Parse(Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/')
                .PadRight(part.Length + (4 - part.Length % 4) % 4, '=')));
            return payload.RootElement.TryGetProperty("aud", out var aud) && aud.ValueKind == JsonValueKind.String && aud.GetString() == audience;
        }
        catch (Exception) { return false; }
    }
}
