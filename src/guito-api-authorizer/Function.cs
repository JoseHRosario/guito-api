using System.Security.Cryptography;
using System.Text;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace GuitoApiAuthorizer
{
    /// <summary>
    /// API Gateway HTTP API REQUEST authorizer for the agent key path (ADR-0003, issues #3/#4):
    /// a request with a valid X-Api-Key gets an Allow policy; missing/unknown keys get Deny.
    /// Independent of the Google token authorizer, which arrives as a separate function.
    /// </summary>
    public class Function
    {
        public const string ApiKeyHeaderName = "X-Api-Key";

        private readonly IKeysLoader _keysLoader;

        public Function() : this(new SecretsManagerKeysLoader(
            Environment.GetEnvironmentVariable("SECRETS_SECRET_NAME") ?? "guito-api/prod"))
        {
        }

        /// <summary>Test seam: inject a fake keys loader.</summary>
        public Function(IKeysLoader keysLoader)
        {
            _keysLoader = keysLoader;
        }

        public async Task<APIGatewayCustomAuthorizerV2IamResponse> FunctionHandler(
            APIGatewayCustomAuthorizerV2Request request, ILambdaContext context)
        {
            var methodArn = request.RouteArn
                ?? $"arn:aws:execute-api:*:{Environment.GetEnvironmentVariable("AWS_REGION") ?? "*"}";

            // Fail closed: any problem loading keys denies the request.
            IReadOnlyList<string> keys;
            try
            {
                keys = await _keysLoader.LoadAsync();
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Authorizer could not load keys: {ex.Message}");
                return Deny(methodArn);
            }

            var provided = GetHeaderValue(request, ApiKeyHeaderName);
            if (provided is null)
            {
                context.Logger.LogWarning("Missing X-Api-Key header: Deny");
                return Deny(methodArn);
            }

            if (!keys.Any(k => FixedTimeEquals(k, provided)))
            {                context.Logger.LogWarning("Unknown X-Api-Key: ***");
                return Deny(methodArn);
            }

            return Allow(methodArn);
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

        private static bool FixedTimeEquals(string expected, string actual)
        {
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var actualBytes = Encoding.UTF8.GetBytes(actual);
            return expectedBytes.Length == actualBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        private static APIGatewayCustomAuthorizerV2IamResponse Allow(string methodArn) => Policy(methodArn, "Allow");

        private static APIGatewayCustomAuthorizerV2IamResponse Deny(string methodArn) => Policy(methodArn, "Deny");

        private static APIGatewayCustomAuthorizerV2IamResponse Policy(string methodArn, string effect) =>
            new()
            {
                PrincipalID = "agent",
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
