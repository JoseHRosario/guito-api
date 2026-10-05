using System.Net;

namespace GuitoApi.Tests;

/// <summary>
/// Boundary tests for the build-version stamp (issue #75). CI injects the
/// version at publish time (deploy/version.sh → -p:InformationalVersion) and
/// every response carries it as the X-Api-Version header via
/// VersionHeaderMiddleware — /healthz itself stays a plain health probe with
/// no version in its body. In the hermetic suite the stamp is whatever the
/// local assembly carries (default csproj version), so the contract asserted
/// here is presence, not a specific value.
/// </summary>
public class HealthzBoundaryTests
{
    [Fact]
    public async Task Healthz_ShouldExposeApiVersionHeader_WhenCalledAnonymously()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/healthz");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var header = response.Headers.TryGetValues("X-Api-Version", out var values)
            ? values.FirstOrDefault()
            : null;
        Assert.False(string.IsNullOrWhiteSpace(header), "X-Api-Version must be present on every response");
    }
}
