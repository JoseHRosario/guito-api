using System.Reflection;
using GuitoApiAuthorizer.AgentKey;

namespace GuitoApiAuthorizer.Tests;

public class AuthorizerSecretsConfigurationTests
{
    [Theory]
    [InlineData("AwsSsm", "/guito-api/staging", typeof(SsmParameterStoreKeysLoader), "/guito-api/staging")]
    [InlineData("AwsSsm", null, typeof(SsmParameterStoreKeysLoader), "/guito-api/prod")]
    [InlineData(null, null, typeof(SecretsManagerKeysLoader), "guito-api/prod")]
    [InlineData("Aws", "guito-api/staging", typeof(SecretsManagerKeysLoader), "guito-api/staging")]
    [InlineData("unknown", null, typeof(SecretsManagerKeysLoader), "guito-api/prod")]
    public void DefaultAgentKeyValidator_ShouldSelectConfiguredLoader_WhenSecretsEnvironmentIsSet(
        string? location, string? secretName, Type expectedType, string expectedName)
    {
        // Arrange
        using var environment = new SecretsEnvironment(location, secretName);

        // Act
        var validator = typeof(Function).GetMethod("DefaultAgentKeyValidator",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var loader = FieldsOf(validator).OfType<IKeysLoader>().Single();
        using var client = FieldsOf(loader).OfType<IDisposable>().SingleOrDefault();

        // Assert
        Assert.IsType(expectedType, loader);
        Assert.Contains(expectedName, FieldsOf(loader).OfType<string>());
    }

    // Inspect composition without invoking either AWS provider or widening the production API.
    private static IEnumerable<object?> FieldsOf(object target) =>
        target.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(field => field.GetValue(target));

    private sealed class SecretsEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _original;

        public SecretsEnvironment(string? location, string? secretName)
        {
            var environment = new Dictionary<string, string?>
            {
                ["SECRETS_LOCATION"] = location,
                ["SECRETS_SECRET_NAME"] = secretName,
                ["AWS_REGION"] = "eu-west-1",
                ["AWS_ACCESS_KEY_ID"] = "hermetic",
                ["AWS_SECRET_ACCESS_KEY"] = "hermetic"
            };
            _original = environment.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
            foreach (var (key, value) in environment)
                Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            foreach (var (key, value) in _original)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
