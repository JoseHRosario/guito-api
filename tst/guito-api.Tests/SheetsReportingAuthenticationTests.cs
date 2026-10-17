using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GuitoApi.Tests;

public class SheetsReportingAuthenticationTests
{
    [Fact]
    public async Task Reporting_ShouldReadHistory_WhenSignedTokenMatchesInstallerAsync()
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = new CustomWebApplicationFactory { Postgres = { PersistExpenses = true } };
        using var configured = ConfigureReporting(factory, transport);
        using var client = CreateReportingClient(configured, transport.Token());

        // Act
        using var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, transport.Requests);
    }

    [Fact]
    public async Task Reporting_ShouldReadHistory_WhenOnlyStandardBearerHeaderArrivesAsync()
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = new CustomWebApplicationFactory { Postgres = { PersistExpenses = true } };
        using var configured = ConfigureReporting(factory, transport);
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", transport.Token());

        // Act
        using var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, transport.Requests);
    }

    [Theory]
    [InlineData("POST", "/Expense/history")]
    [InlineData("OPTIONS", "/Expense/history")]
    [InlineData("GET", "/Expense/latest/5")]
    [InlineData("GET", "/Expense/history/child")]
    [InlineData("DELETE", "/Smoke/1")]
    [InlineData("GET", "/Expense/7")]
    [InlineData("PUT", "/Expense/7")]
    [InlineData("DELETE", "/Expense/7")]
    [InlineData("POST", "/BankTransaction/1/create-and-match")]
    [InlineData("GET", "/BankTransaction/1/create-and-match/11111111-2222-3333-4444-555555555555")]
    public async Task Reporting_ShouldDenyOtherOperations_WhenOnlyBearerIsPresentedAsync(string method, string route)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = new CustomWebApplicationFactory();
        using var configured = ConfigureReporting(factory, transport);
        using var client = CreateReportingClient(configured, transport.Token());
        client.DefaultRequestHeaders.Remove("x-google-idtoken");
        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoDataAccess(factory);
    }

    [Theory]
    [InlineData("sub", "different-installer")]
    [InlineData("email_verified", false)]
    [InlineData("iss", "https://evil.example")]
    [InlineData("exp", 1L)]
    public async Task Reporting_ShouldDenyInvalidIdentity_WhenOnlyBearerIsPresentedAsync(string claim, object value)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = new CustomWebApplicationFactory();
        using var configured = ConfigureReporting(factory, transport);
        using var client = CreateReportingClient(configured, transport.Token(payload => payload[claim] = value));
        client.DefaultRequestHeaders.Remove("x-google-idtoken");

        // Act
        using var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoDataAccess(factory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("different-token")]
    public async Task Reporting_ShouldDenyHistory_WhenPresentDuplicateHeaderDoesNotMatchAsync(string duplicate)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = new CustomWebApplicationFactory();
        using var configured = ConfigureReporting(factory, transport, receiveEmptyDuplicate: duplicate.Length == 0);
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", transport.Token());
        client.DefaultRequestHeaders.TryAddWithoutValidation("x-google-idtoken", duplicate);

        // Act
        using var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoDataAccess(factory);
    }

    [Theory]
    [InlineData("GET", "/expense/HISTORY", 200)]
    [InlineData("POST", "/Expense/history", 403)]
    [InlineData("OPTIONS", "/Expense/history", 403)]
    [InlineData("GET", "/Expense", 403)]
    [InlineData("GET", "/Expense/history/child", 403)]
    [InlineData("GET", "/Expense/history/", 403)]
    [InlineData("GET", "/Expense/history-more", 403)]
    [InlineData("POST", "/Auth/logout", 403)]
    [InlineData("DELETE", "/Smoke/1", 403)]
    [InlineData("GET", "/Expense/7", 403)]
    [InlineData("PUT", "/Expense/7", 403)]
    [InlineData("DELETE", "/Expense/7", 403)]
    [InlineData("POST", "/BankTransaction/1/create-and-match", 403)]
    [InlineData("GET", "/BankTransaction/1/create-and-match/11111111-2222-3333-4444-555555555555", 403)]
    public async Task Reporting_ShouldAuthorizeOnlyExactHistoryGet_WhenRequestRouteVariesAsync(string method, string route, int status)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = new CustomWebApplicationFactory { Postgres = { PersistExpenses = true } };
        using var configured = ConfigureReporting(factory, transport);
        using var client = CreateReportingClient(configured, transport.Token());
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        // A valid agent credential must not rescue a reporting identity on another route.
        request.Headers.Add("X-Api-Key", new FakeSecretsProvider().Payload.ApiKeys.Single());

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        if (status == 403) AssertNoDataAccess(factory);
    }

    [Theory]
    [InlineData("different-token")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Reporting_ShouldDenyHistory_WhenBearerIsMissingOrMismatchedAsync(string? bearer)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = new CustomWebApplicationFactory();
        using var configured = ConfigureReporting(factory, transport);
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Add("x-google-idtoken", transport.Token());
        if (bearer is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");

        // Act
        using var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Reporting_ShouldDenyHistory_WhenSubjectConfigurationIsPartialAsync(string subject)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = new CustomWebApplicationFactory();
        using var configured = ConfigureReporting(factory, transport, subject: subject);
        using var client = CreateReportingClient(configured, transport.Token());

        // Act
        using var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, transport.Requests);
    }

    [Fact]
    public async Task Reporting_ShouldDenyHistory_WhenUiAudienceOverlapsAsync()
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = new CustomWebApplicationFactory();
        using var configured = ConfigureReporting(factory, transport, humanAudience: ReportingTokenFixture.Audience);
        using var client = CreateReportingClient(configured, transport.Token());

        // Act
        using var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, transport.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reporting_ShouldDenyHistory_WhenSignatureUsesUnknownKeyAsync(bool duplicateHeader)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = new CustomWebApplicationFactory();
        using var configured = ConfigureReporting(factory, transport);
        using var client = CreateReportingClient(configured, transport.Token(wrongSignature: true));
        if (!duplicateHeader) client.DefaultRequestHeaders.Remove("x-google-idtoken");

        // Act
        using var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData("sub", "other-subject")]
    [InlineData("sub", null)]
    [InlineData("email_verified", false)]
    [InlineData("email_verified", "true")]
    [InlineData("email_verified", null)]
    [InlineData("email", null)]
    [InlineData("iss", "https://evil.example")]
    [InlineData("exp", 1L)]
    public async Task Reporting_ShouldDenyHistory_WhenSignedIdentityIsInvalidAsync(string claim, object? value)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = new CustomWebApplicationFactory { Postgres = { PersistExpenses = true } };
        using var configured = ConfigureReporting(factory, transport, validateIdToken: false, validateApiKey: false);
        using var client = CreateReportingClient(configured, transport.Token(p => { if (value is null) p.Remove(claim); else p[claim] = value; }));

        // Act
        using var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task Reporting_ShouldRejectPost_WhenPresentedWithReportingAudienceAsync()
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = new CustomWebApplicationFactory { Postgres = { PersistExpenses = true } };
        using var configured = ConfigureReporting(factory, transport, validateIdToken: false, validateApiKey: false);
        using var client = CreateReportingClient(configured, UnsignedReportingToken());

        // Act
        using var response = await client.PostAsync("/Expense/history", new StringContent("{}"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static WebApplicationFactory<Program> ConfigureReporting(
        CustomWebApplicationFactory factory, ReportingTokenFixture transport, string subject = ReportingTokenFixture.Subject,
        string humanAudience = "ui-client", bool validateIdToken = true, bool validateApiKey = true,
        bool receiveEmptyDuplicate = false) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfiguration:SheetsReportingAuthentication:Audience"] = ReportingTokenFixture.Audience,
                ["AppConfiguration:SheetsReportingAuthentication:Subject"] = subject,
                ["AppConfiguration:Authentication:OAuthAudience"] = humanAudience,
                ["AppConfiguration:Authentication:ValidateIdToken"] = validateIdToken.ToString(),
                ["AppConfiguration:Authentication:ValidateApiKey"] = validateApiKey.ToString(),
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("SheetsReportingJwks").ConfigurePrimaryHttpMessageHandler(() => transport);
                if (receiveEmptyDuplicate) services.AddSingleton<IStartupFilter>(new EmptyDuplicateHeaderStartupFilter());
            });
        });

    private static HttpClient CreateReportingClient(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("x-google-idtoken", token);
        return client;
    }

    private static void AssertNoDataAccess(CustomWebApplicationFactory factory)
    {
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    // HttpClient omits empty request headers; replay the received-header state at the host boundary.
    private sealed class EmptyDuplicateHeaderStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Request.Headers["x-google-idtoken"] = new Microsoft.Extensions.Primitives.StringValues(string.Empty);
                return continuation(context);
            });
            next(app);
        };
    }

    private static string UnsignedReportingToken()
    {
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{{\"aud\":\"{ReportingTokenFixture.Audience}\"}}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"e30.{payload}.invalid";
    }
}
