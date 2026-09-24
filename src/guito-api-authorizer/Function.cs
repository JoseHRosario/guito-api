using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using GuitoApiAuthorizer.AgentKey;
using GuitoApiAuthorizer.GoogleToken;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace GuitoApiAuthorizer
{
    /// <summary>
    /// API Gateway HTTP API REQUEST authorizer (ADR-0003, issues #3/#4/#13). API Gateway
    /// allows one CUSTOM authorizer per route, so this is a thin dispatcher over two
    /// INDEPENDENT validators; never let one path fall back into the other:
    ///   - X-Api-Key header present  → agent-key validation (AgentKey/, fixed-time compare).
    ///   - x-google-idtoken present  → Google ID-token validation (GoogleToken/, RS256/JWKS).
    ///   - neither header            → Deny. Any validation problem → Deny (fail closed).
    /// The in-app middlewares (ApiKeyMiddleware, GoogleIdTokenMiddleware) repeat the checks
    /// as defense-in-depth and stay path-independent.
    /// </summary>
    public class Function
    {
        public const string ApiKeyHeaderName = "X-Api-Key";
        public const string GoogleTokenHeaderName = "x-google-idtoken";
        public const string GoogleClientIdEnvVar = "GOOGLE_CLIENT_ID";
        public const string GoogleAllowedEmailsEnvVar = "GOOGLE_ALLOWED_EMAILS";

        private readonly IAgentKeyValidator _agentKeyValidator;
        private readonly IGoogleTokenValidator _googleTokenValidator;

        public Function() : this(
            new AgentKeyValidator(
                new SecretsManagerKeysLoader(
                    Environment.GetEnvironmentVariable("SECRETS_SECRET_NAME") ?? "guito-api/prod")))
        {
            _googleTokenValidator ??= new GoogleTokenValidator(
                new HttpJwksClient(),
                Environment.GetEnvironmentVariable(GoogleClientIdEnvVar) ?? string.Empty,
                (Environment.GetEnvironmentVariable(GoogleAllowedEmailsEnvVar) ?? string.Empty).Split(','));
        }

        /// <summary>Test seam: inject fake validators for either path.</summary>
        public Function(IAgentKeyValidator agentKeyValidator, IGoogleTokenValidator? googleTokenValidator = null)
        {
            _agentKeyValidator = agentKeyValidator;
            _googleTokenValidator = googleTokenValidator ?? new GoogleTokenValidator(
                new HttpJwksClient(),
                Environment.GetEnvironmentVariable(GoogleClientIdEnvVar) ?? string.Empty,
                (Environment.GetEnvironmentVariable(GoogleAllowedEmailsEnvVar) ?? string.Empty).Split(','));
        }

        public async Task<APIGatewayCustomAuthorizerV2IamResponse> FunctionHandler(
            APIGatewayCustomAuthorizerV2Request request, ILambdaContext context)
        {
            var methodArn = request.RouteArn
                ?? $"arn:aws:execute-api:*:{Environment.GetEnvironmentVariable("AWS_REGION") ?? "*"}";

            var googleToken = GetHeaderValue(request, GoogleTokenHeaderName);
            if (googleToken is not null)
            {
                var result = await _googleTokenValidator.ValidateAsync(googleToken);
                if (result.Valid)
                {
                    context.Logger.LogInformation($"Google token accepted for email: {result.Claims!.Email}");
                    return Allow(methodArn, "human");
                }
                context.Logger.LogWarning($"Google token rejected: {result.FailureReason}");
                return Deny(methodArn);
            }

            var agentResult = await _agentKeyValidator.ValidateAsync(GetHeaderValue(request, ApiKeyHeaderName));
            if (agentResult.Valid)
                return Allow(methodArn, "agent");

            context.Logger.LogWarning($"X-Api-Key rejected: {agentResult.FailureReason}");
            return Deny(methodArn);
        }

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
            Policy(methodArn, "Allow", principal);

        private static APIGatewayCustomAuthorizerV2IamResponse Deny(string methodArn) =>
            Policy(methodArn, "Deny", "agent");

        private static APIGatewayCustomAuthorizerV2IamResponse Policy(string methodArn, string effect, string principal) =>
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
            };
    }
}