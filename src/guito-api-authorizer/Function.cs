using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using GuitoApiAuthorizer.AgentKey;
using GuitoApiAuthorizer.GoogleToken;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace GuitoApiAuthorizer
{
    /// <summary>
    /// API Gateway HTTP API REQUEST authorizer (ADR-0003, issues #3/#4/#13). API Gateway
    /// allows one CUSTOM authorizer per route, and `Authorization` is the gateway's SOLE
    /// identity source — so this dispatches on that header alone and never falls back:
    ///   - `Authorization: Bearer <jwt>` → Google ID-token validation (GoogleToken/).
    ///   - any other raw value           → agent-key validation (AgentKey/).
    ///   - no Authorization header       → Deny. Any validation problem → Deny (fail closed).
    /// Other headers (X-Api-Key, x-google-idtoken) are NOT gateway identity sources and are
    /// never forwarded to REQUEST authorizers — dispatching on them here is dead code. The
    /// in-app middlewares (ApiKeyMiddleware, GoogleIdTokenMiddleware) repeat the checks as
    /// defense-in-depth.
    /// </summary>
    public class Function
    {
        public const string AuthorizationHeaderName = "Authorization";
        public const string BearerPrefix = "Bearer ";
        public const string GoogleClientIdEnvVar = "GOOGLE_CLIENT_ID";
        public const string GoogleAllowedEmailsEnvVar = "GOOGLE_ALLOWED_EMAILS";

        private readonly IAgentKeyValidator _agentKeyValidator;
        private readonly IGoogleTokenValidator _googleTokenValidator;

        public Function() : this(DefaultAgentKeyValidator(), DefaultGoogleTokenValidator())
        {
        }

        /// <summary>Test seam: inject fake validators for either path.</summary>
        public Function(IAgentKeyValidator agentKeyValidator, IGoogleTokenValidator? googleTokenValidator = null)
        {
            _agentKeyValidator = agentKeyValidator;
            _googleTokenValidator = googleTokenValidator ?? DefaultGoogleTokenValidator();
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

            return authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
                ? await AuthorizeGoogleAsync(authorization[BearerPrefix.Length..], methodArn, context)
                : await AuthorizeAgentAsync(authorization, methodArn, context);
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

        private static AgentKeyValidator DefaultAgentKeyValidator() =>
            new(new SecretsManagerKeysLoader(
                Environment.GetEnvironmentVariable("SECRETS_SECRET_NAME") ?? "guito-api/prod"));

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