using System.Security.Cryptography;
using System.Text;

namespace GuitoApiAuthorizer.AgentKey
{
    /// <summary>
    /// Validates the provided X-Api-Key against the keys loaded from Secrets Manager
    /// (IKeysLoader) with a fixed-time comparison. Independent of the Google token path
    /// (ADR-0003); fail-closed: missing key, unknown key, or any key-loading problem denies.
    /// </summary>
    public class AgentKeyValidator(IKeysLoader keysLoader) : IAgentKeyValidator
    {
        public async Task<AgentKeyResult> ValidateAsync(string? provided)
        {
            if (string.IsNullOrEmpty(provided))
                return Fail("missing X-Api-Key");

            // Fail closed: any problem loading keys denies the request.
            IReadOnlyList<string> keys;
            try
            {
                keys = await keysLoader.LoadAsync();
            }
            catch (Exception ex)
            {
                return Fail($"could not load agent keys: {ex.Message}");
            }

            if (!keys.Any(k => FixedTimeEquals(k, provided)))
                return Fail("unknown X-Api-Key");

            return new AgentKeyResult(true, null);
        }

        private static bool FixedTimeEquals(string expected, string actual)
        {
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var actualBytes = Encoding.UTF8.GetBytes(actual);
            return expectedBytes.Length == actualBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        private static AgentKeyResult Fail(string reason) => new(false, reason);
    }
}