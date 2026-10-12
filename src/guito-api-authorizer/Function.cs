using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.SimpleSystemsManagement;
using GuitoApiAuthorizer.AgentKey;
using GuitoApiAuthorizer.GoogleToken;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace GuitoApiAuthorizer
{
    /// <summary>
    /// API Gateway HTTP API REQUEST authorizer (ADR-0003, issues #3/#4/#13). API Gateway
    /// allows one CUSTOM authorizer per route, and `Authorization` is the gateway's SOLE
    /// identity source — so this dispatches on that header alone and never falls back:
    ///   - `Authorization: Bearer *** → independent human or reporting Google ID-token policy.
    ///   - any other raw value           → agent-key validation (AgentKey/).
    ///   - no Authorization header       → Deny. Any validation problem → Deny (fail closed).
    /// Other headers (X-Api-Key, x-google-idtoken) are NOT gateway identity sources.
    /// Reporting dispatch uses Authorization only; if a second token is forwarded, it must match. The
    /// in-app middlewares (ApiKeyMiddleware, GoogleIdTokenMiddleware) repeat the checks as
    /// defense-in-depth.
    /// </summary>
    public class Function
    {
        public const string AuthorizationHeaderName = "Authorization";
        public const string BearerPrefix = "Bearer ";
        public const string GoogleClientIdEnvVar = "GOOGLE_CLIENT_ID";
        public const string GoogleAllowedEmailsEnvVar = "GOOGLE_ALLOWED_EMAILS";

        public const string SheetsReportingAudienceEnvVar = "SHEETS_REPORTING_GOOGLE_AUDIENCE";
        public const string SheetsReportingSubjectEnvVar = "SHEETS_REPORTING_GOOGLE_SUBJECT";
        private readonly string _reportingAudience;
        private readonly string _reportingSubject;
        private readonly string _humanAudience;
        private readonly IGoogleTokenValidator _reportingValidator;
        private readonly IAgentKeyValidator _agentKeyValidator;
        private readonly IGoogleTokenValidator _googleTokenValidator;

        public Function() : this(DefaultAgentKeyValidator(), DefaultGoogleTokenValidator())
        {
        }

        /// <summary>Test seam: inject validators or reporting JWKS transport and explicit public identity selectors.</summary>
        public Function(IAgentKeyValidator agentKeyValidator, IGoogleTokenValidator? googleTokenValidator = null,
            IJwksClient? reportingJwksClient = null, string? reportingAudience = null, string? reportingSubject = null, string? humanAudience = null)
        {
            _agentKeyValidator = agentKeyValidator;
            _googleTokenValidator = googleTokenValidator ?? DefaultGoogleTokenValidator();
            _reportingAudience = reportingAudience ?? Environment.GetEnvironmentVariable(SheetsReportingAudienceEnvVar) ?? string.Empty;
            _reportingSubject = reportingSubject ?? Environment.GetEnvironmentVariable(SheetsReportingSubjectEnvVar) ?? string.Empty;
            _humanAudience = humanAudience ?? Environment.GetEnvironmentVariable(GoogleClientIdEnvVar) ?? string.Empty;
            _reportingValidator = new GoogleTokenValidator(reportingJwksClient ?? new HttpJwksClient(), _reportingAudience, [],
                expectedSubject: _reportingSubject, requireVerifiedEmail: true);
        }

        public async Task<APIGatewayCustomAuthorizerV2IamResponse> FunctionHandlerAsync(
            APIGatewayCustomAuthorizerV2Request request, ILambdaContext context)
        {
            var methodArn = request.RouteArn
                ?? $"arn:aws:execute-api:*:{Environment.GetEnvironmentVariable("AWS_REGION") ?? "*"}";

            // Gateway identity source is a single header: Authorization. Bearer <jwt> →
            // Google path; any other raw value → agent key. Neither → Deny.
            var authorization = GetHeaderValue(request, AuthorizationHeaderName);
            if (authorization is null)
                return Deny(methodArn, "anonymous", "no Authorization header");

            if (authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var token = authorization[BearerPrefix.Length..];
                if (HasReportingAudience(token))
                    return await AuthorizeReportingAsync(token, request, methodArn);
                return await AuthorizeGoogleAsync(token, methodArn, context);
            }
            return await AuthorizeAgentAsync(authorization, methodArn, context);
        }

        private bool HasReportingAudience(string token)
        {
            if (string.IsNullOrWhiteSpace(_reportingAudience)) return false;
            try
            {
                var part = token.Split('.')[1];
                using var payload = JsonDocument.Parse(GoogleTokenValidator.Base64UrlDecodeString(part));
                return payload.RootElement.TryGetProperty("aud", out var aud) && aud.ValueKind == JsonValueKind.String && aud.GetString() == _reportingAudience;
            }
            catch (Exception) { return false; }
        }

        private async Task<APIGatewayCustomAuthorizerV2IamResponse> AuthorizeReportingAsync(
            string token, APIGatewayCustomAuthorizerV2Request request, string methodArn)
        {
            // HTTP context is authoritative. $default RouteArn is not a URL suffix.
            var http = request.RequestContext?.Http;
            if (string.IsNullOrWhiteSpace(_reportingSubject) || _reportingAudience == _humanAudience
                || http?.Method != "GET" || !string.Equals(http.Path, "/Expense/history", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(request.RouteArn))
                return Deny(methodArn, "sheets-reporting", "reporting scope or configuration rejected");
            var otherToken = GetHeaderValue(request, "x-google-idtoken");
            if (otherToken is not null && otherToken != token)
                return Deny(methodArn, "sheets-reporting", "reporting headers disagree");
            try
            {
                var result = await _reportingValidator.ValidateAsync(token);
                return result.Valid ? Allow(methodArn, "sheets-reporting") : Deny(methodArn, "sheets-reporting", result.FailureReason);
            }
            catch (Exception)
            {
                return Deny(methodArn, "sheets-reporting", "reporting validation failed");
            }
        }

        private async Task<APIGatewayCustomAuthorizerV2IamResponse> AuthorizeGoogleAsync(
            string idToken, string methodArn, ILambdaContext context)
        {
            var result = await _googleTokenValidator.ValidateAsync(idToken);
            if (result.Valid)
            {
                context.Logger.LogInformation($"Google token accepted for email: {result.Claims!.Email}");
                return Allow(methodArn, "human");
            }
            context.Logger.LogWarning($"Google token rejected: {result.FailureReason}");
            return Deny(methodArn, "human", result.FailureReason);
        }

        private async Task<APIGatewayCustomAuthorizerV2IamResponse> AuthorizeAgentAsync(
            string providedKey, string methodArn, ILambdaContext context)
        {
            var result = await _agentKeyValidator.ValidateAsync(providedKey);
            if (result.Valid)
            {
                context.Logger.LogInformation("Agent key accepted");
                return Allow(methodArn, "agent");
            }
            context.Logger.LogWarning($"Agent key rejected: {result.FailureReason}");
            return Deny(methodArn, "agent", result.FailureReason);
        }

        private static AgentKeyValidator DefaultAgentKeyValidator()
        {
            if (Environment.GetEnvironmentVariable("SECRETS_LOCATION") != "AwsSsm")
                throw new InvalidOperationException("SECRETS_LOCATION must explicitly be AwsSsm.");

            var parameterName = Environment.GetEnvironmentVariable("SECRETS_SECRET_NAME");
            if (string.IsNullOrWhiteSpace(parameterName))
                throw new InvalidOperationException("SECRETS_SECRET_NAME must specify an SSM parameter name.");
            var loader = new SsmParameterStoreKeysLoader(parameterName,
                new AmazonSimpleSystemsManagementClient(new AmazonSimpleSystemsManagementConfig()));
            return new AgentKeyValidator(loader);
        }

        private static GoogleTokenValidator DefaultGoogleTokenValidator() =>
            new(new HttpJwksClient(),
                Environment.GetEnvironmentVariable(GoogleClientIdEnvVar) ?? string.Empty,
                (Environment.GetEnvironmentVariable(GoogleAllowedEmailsEnvVar) ?? string.Empty).Split(','));

        private static string? GetHeaderValue(APIGatewayCustomAuthorizerV2Request request, string name)
        {
            if (request.Headers is null)
                return null;

            foreach (var pair in request.Headers)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
            return null;
        }

        private static APIGatewayCustomAuthorizerV2IamResponse Allow(string methodArn, string principal) =>
            Policy(methodArn, "Allow", principal, null);

        private static APIGatewayCustomAuthorizerV2IamResponse Deny(
            string methodArn, string principal, string? reason) =>
            Policy(methodArn, "Deny", principal, reason);

        private static APIGatewayCustomAuthorizerV2IamResponse Policy(
            string methodArn, string effect, string principal, string? reason) =>
            new()
            {
                PrincipalID = principal,
                PolicyDocument = new APIGatewayCustomAuthorizerPolicy
                {
                    Version = "2012-10-17",
                    Statement =
                    [
                        new APIGatewayCustomAuthorizerPolicy.IAMPolicyStatement
                        {
                            Action = ["execute-api:Invoke"],
                            Effect = effect,
                            Resource = [methodArn],
                        },
                    ],
                },
                // Context carries the rejection reason for CloudWatch/edge debugging.
                Context = reason is null
                    ? new Dictionary<string, object>()
                    : new Dictionary<string, object> { ["DenyReason"] = reason },
            };
    }
}