using System.Net;
using System.Text.Json;
using GuitoApi.IntegrationTests.Common;

namespace GuitoApi.IntegrationTests;

[Trait(DeployedEndpointFixture.CategoryTrait, DeployedEndpointFixture.CategoryValue)]
public class DeployedExpenseReadTests
{
    [Fact]
    public async Task Categories_ShouldReturnNameOnlyRows_WhenReadingPostgres()
    {
        using var client = DeployedEndpointFixture.CreateAgentClient(DeployedEndpointFixture.AgentKey);
        using var response = await client.GetAsync("/Category");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var category in document.RootElement.GetProperty("categories").EnumerateArray())
        {
            Assert.Single(category.EnumerateObject());
            Assert.Equal(JsonValueKind.String, category.GetProperty("name").ValueKind);
        }
        // Ordering follows the database collation, not this host's culture.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_ShouldSucceed_WhenReadingWithSupportedAuth(bool human)
    {
        using var client = human
            ? DeployedEndpointFixture.CreateGoogleClient(DeployedEndpointFixture.GoogleIdToken)
            : DeployedEndpointFixture.CreateAgentClient(DeployedEndpointFixture.AgentKey);
        using var response = await client.GetAsync("/Expense/history?pageSize=7&pageIndex=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

}
