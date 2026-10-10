namespace GuitoApiAuthorizer.Tests;

public class AuthorizerSecretsConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Aws")]
    [InlineData("unknown")]
    [InlineData("awsssm")]
    public void Constructor_ShouldRejectBackend_WhenLocationIsMissingOrUnsupported(string? location)
    {
        // Arrange
        using var environment = new SecretsEnvironment(location, "/guito-api/staging");

        // Act
        var error = Assert.Throws<InvalidOperationException>(() => new Function());

        // Assert
        Assert.Contains("SECRETS_LOCATION", error.Message);
        Assert.Contains("AwsSsm", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_ShouldRejectParameterName_WhenNameIsMissing(string? parameterName)
    {
        // Arrange
        using var environment = new SecretsEnvironment("AwsSsm", parameterName);

        // Act
        var error = Assert.Throws<InvalidOperationException>(() => new Function());

        // Assert
        Assert.Contains("SECRETS_SECRET_NAME", error.Message);
        Assert.Contains("SSM parameter name", error.Message);
    }

    [Fact]
    public void Constructor_ShouldAcceptConfiguration_WhenSsmParameterIsExplicit()
    {
        // Arrange
        using var environment = new SecretsEnvironment("AwsSsm", "/guito-api/staging");

        // Act
        var error = Record.Exception(() => new Function());

        // Assert
        Assert.Null(error);
    }

    private sealed class SecretsEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _original;

        public SecretsEnvironment(string? location, string? parameterName)
        {
            var environment = new Dictionary<string, string?>
            {
                ["SECRETS_LOCATION"] = location,
                ["SECRETS_SECRET_NAME"] = parameterName,
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
