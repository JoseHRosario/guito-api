using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.Infrastructure.Postgres;
using Microsoft.Extensions.Configuration;

namespace GuitoApi.Tests;

public class PostgresExpenseBoundaryTests
{
    [Fact]
    public async Task Latest_ShouldMapPostgresTextValues_WhenDataApiReturnsUtcInstantsAndNumericStrings()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.NextResults.Enqueue(new PostgresResult([], [[
            PostgresValue.FromString("d00ad366-8d5b-4a66-a2c6-bc09e217dd07"),
            PostgresValue.FromString("2026-07-09 23:00:00+00"),
            PostgresValue.FromString("79228162514264337593543950335"),
            PostgresValue.FromString("EUR"), PostgresValue.FromString("Unchanged"),
            PostgresValue.FromString("Groceries"), PostgresValue.FromString("jose@example.test"),
            PostgresValue.FromString("2026-10-10 20:00:00.123456+00"),
            PostgresValue.FromString("2026-10-10 20:00:00.123456+00")]], 0));
        using var client = factory.CreateClient();

        // Act
        var latest = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1")).GetProperty("expenses");

        // Assert
        var expense = Assert.Single(latest.EnumerateArray());
        Assert.Equal(decimal.MaxValue, expense.GetProperty("amount").GetDecimal());
        Assert.Equal("2026-07-10T00:00:00+01:00", expense.GetProperty("occurredAt").GetDateTimeOffset().ToString("yyyy-MM-ddTHH:mm:sszzz"));
        Assert.Equal("jose@example.test", expense.GetProperty("creatorEmail").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T21:00:00.123456+01:00"), expense.GetProperty("creationDate").GetDateTimeOffset());
        Assert.False(expense.TryGetProperty("storedOrder", out _));
    }

    [Theory]
    [InlineData("POST", "/Expense")]
    [InlineData("GET", "/Expense/latest/1")]
    [InlineData("DELETE", "/Smoke/opaque-not-a-number")]
    public async Task ExpenseEndpoints_ShouldRejectUnauthenticatedRequests_WhenPostgresSelected(string method, string path)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        using var secured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AppConfiguration:Authentication:ValidateApiKey"] = "true" })));
        using var client = secured.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task CreateExpense_ShouldSurfaceFailureWithoutSuccessIdentity_WhenDataApiFails()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.ExecuteException = new InvalidOperationException("hermetic datastore failure");
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { date = "2026-10-10", amount = 1m, description = "Test", category = "Test" });

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("\"id\"", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.SheetsHandler.AppendBodies);
    }

    [Fact]
    public async Task CreateExpense_ShouldRejectMissingDate_WhenNoLegacyDateReceived()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { amount = 1m, description = "Test", category = "Test" });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Latest_ShouldRejectUnboundedCount_WhenOutsideSupportedRange(int count)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/Expense/latest/{count}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task CreateExpense_ShouldRefusePostgres_WhenProductionRequestsUnapprovedCutover()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Environment = "Production" };
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-agent-key");

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { date = "2026-10-10", amount = 1m, description = "Test", category = "Test" });

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.AppendBodies);
    }

    [Theory]
    [InlineData("2026-07-01T23:30:00.123456Z", "2026-07-02T00:30:00.123456+01:00")]
    [InlineData("2026-07-02T09:30:00.123456+10:00", "2026-07-02T00:30:00.123456+01:00")]
    public async Task CreateAndList_ShouldNormalizeKnownInstant_WhenInputOffsetIsNotLisbon(string occurredAt, string expected)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.PersistExpenses = true;
        using var client = factory.CreateClient();

        // Act
        var created = await client.PostAsJsonAsync("/Expense", new { date = "2026-07-02", occurredAt, amount = 1m });

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var latest = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1")).GetProperty("expenses");
        var expense = Assert.Single(latest.EnumerateArray());
        Assert.Equal(DateTimeOffset.Parse(occurredAt).UtcTicks, expense.GetProperty("occurredAt").GetDateTimeOffset().UtcTicks);
        Assert.Equal(DateTimeOffset.Parse(expected).ToString("O"), expense.GetProperty("occurredAt").GetDateTimeOffset().ToString("O"));
        Assert.Equal(new DateTime(2026, 7, 2), expense.GetProperty("date").GetDateTime());
    }

    [Theory]
    [InlineData("2026-03-29", "2026-03-29T01:30:00+00:00", HttpStatusCode.OK)]
    [InlineData("2026-03-29", "2026-03-29T01:30:00+01:00", HttpStatusCode.OK)]
    [InlineData("2026-10-25", "2026-10-25T01:30:00+01:00", HttpStatusCode.OK)]
    [InlineData("2026-10-25", "2026-10-25T01:30:00+00:00", HttpStatusCode.OK)]
    [InlineData("2026-10-10", "2026-10-10T14:00:00Z", HttpStatusCode.OK)]
    [InlineData("2026-10-10", "2026-10-10T14:00:00", HttpStatusCode.BadRequest)]
    [InlineData("2026-10-10", "2026-10-11T14:00:00+01:00", HttpStatusCode.BadRequest)]
    [InlineData("2026-10-10", "2026-10-10T14:00:00.1234567+01:00", HttpStatusCode.BadRequest)]
    [InlineData("2026-10-10", "garbage", HttpStatusCode.BadRequest)]
    [InlineData("2026-07-01", "2026-07-01T23:30:00.123456Z", HttpStatusCode.BadRequest)]
    [InlineData("2026-03-29", "2026-03-29T01:30:00", HttpStatusCode.BadRequest)]
    [InlineData("2026-10-25", "2026-10-25T01:30:00", HttpStatusCode.BadRequest)]
    public async Task CreateExpense_ShouldValidateLisbonDayAndPrecision_WhenExplicitInstantReceived(string date, string occurredAt, HttpStatusCode expected)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.PersistExpenses = true;
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { date, occurredAt, amount = 1m, description = "Test", category = "Test" });

        // Assert
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK) Assert.Empty(factory.Postgres.ExecutedSql);
        else
        {
            var listed = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/10")).GetProperty("expenses");
            Assert.Equal(DateTimeOffset.Parse(occurredAt), Assert.Single(listed.EnumerateArray()).GetProperty("occurredAt").GetDateTimeOffset());
        }
    }

    [Theory]
    [InlineData("2026-01-10", "2026-01-10T00:00:00+00:00")]
    [InlineData("2026-07-10", "2026-07-10T00:00:00+01:00")]
    public async Task CreateAndList_ShouldUseLisbonMidnight_WhenLegacyDateHasNoTime(string date, string expected)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.PersistExpenses = true;
        using var client = factory.CreateClient();

        // Act
        var created = await client.PostAsJsonAsync("/Expense", new { date, amount = 0.0000000000000000000000000001m, description = "Original description", category = "Original category" });
        var listed = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/10")).GetProperty("expenses");

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var expense = Assert.Single(listed.EnumerateArray());
        Assert.Equal(expected, expense.GetProperty("occurredAt").GetDateTimeOffset().ToString("yyyy-MM-ddTHH:mm:sszzz"));
        Assert.Equal(0.0000000000000000000000000001m, expense.GetProperty("amount").GetDecimal());
        Assert.Equal("Original description", expense.GetProperty("description").GetString());
        Assert.Equal("Original category", expense.GetProperty("category").GetString());
        Assert.Equal("xunga.meireles@gmail.com", expense.GetProperty("creatorEmail").GetString());
    }

    [Theory]
    [InlineData("123456789.1234567890123456789")]
    [InlineData("0.0000000000000000000000000001")]
    public async Task CreateAndList_ShouldPreserveExactAmountText_WhenDecimalStringExceedsJavascriptPrecision(string amount)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.PersistExpenses = true;
        using var client = factory.CreateClient();

        // Act
        var created = await client.PostAsJsonAsync("/Expense", new { date = "2026-07-02", amount });

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var latest = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1")).GetProperty("expenses");
        var expense = Assert.Single(latest.EnumerateArray());
        Assert.Equal(JsonValueKind.Number, expense.GetProperty("amount").ValueKind);
        Assert.Equal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), expense.GetProperty("amount").GetDecimal());
        Assert.Equal(amount, expense.GetProperty("amountExact").GetString());
    }

    [Theory]
    [InlineData("\"0.12345678901234567890123456789\"")]
    [InlineData("0.12345678901234567890123456789")]
    [InlineData("\"0.00000000000000000000000000011\"")]
    [InlineData("0.00000000000000000000000000011")]
    [InlineData("\"79228162514264337593543950335.1\"")]
    [InlineData("79228162514264337593543950335.1")]
    [InlineData("\"79228162514264337593543950336\"")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("\"1e-29\"")]
    [InlineData("1e-29")]
    [InlineData("1e999999999999999999999999999999")]
    [InlineData("1e-999999999999999999999999999999")]
    public async Task CreateExpense_ShouldRejectRounding_WhenAmountTokenIsNotExactlyRepresentable(string amountToken)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        using var client = factory.CreateClient();
        using var content = new StringContent($"{{\"date\":\"2026-07-02\",\"amount\":{amountToken}}}", System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/Expense", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData("\"0.1234567890123456789012345678000\"", "0.1234567890123456789012345678")]
    [InlineData("0.1234567890123456789012345678000", "0.1234567890123456789012345678")]
    [InlineData("\"1.230000000000000000000000000000000000\"", "1.23")]
    [InlineData("1.230000000000000000000000000000000000", "1.23")]
    [InlineData("\"123e-2\"", "1.23")]
    [InlineData("123e-2", "1.23")]
    [InlineData("1.23e+2", "123")]
    [InlineData("10e-29", "0.0000000000000000000000000001")]
    [InlineData("\"79228162514264337593543950335\"", "79228162514264337593543950335")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    [InlineData("79228162514264337593543950335.000", "79228162514264337593543950335")]
    [InlineData("\"0.0000000000000000000000000001\"", "0.0000000000000000000000000001")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    public async Task CreateAndList_ShouldPreserveExactValue_WhenAmountTokenHasEquivalentRepresentation(string amountToken, string expected)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.PersistExpenses = true;
        using var client = factory.CreateClient();
        using var content = new StringContent($"{{\"date\":\"2026-07-02\",\"amount\":{amountToken}}}", System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/Expense", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var latest = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1")).GetProperty("expenses");
        var expense = Assert.Single(latest.EnumerateArray());
        Assert.Equal(expected, expense.GetProperty("amountExact").GetString());
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), expense.GetProperty("amount").GetDecimal());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAndList_ShouldPreserveExactValue_WhenAmountTokenSpansRequestBuffers(bool quoted)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.PersistExpenses = true;
        using var client = factory.CreateClient();
        var amount = $"1.23{new string('0', 20000)}";
        var token = quoted ? JsonSerializer.Serialize(amount) : amount;
        using var content = new StringContent($"{{\"date\":\"2026-07-02\",\"amount\":{token}}}", System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/Expense", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var latest = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1")).GetProperty("expenses");
        Assert.Equal("1.23", Assert.Single(latest.EnumerateArray()).GetProperty("amountExact").GetString());
    }

    [Theory]
    [InlineData("1,23")]
    [InlineData("NaN")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("79228162514264337593543950336")]
    public async Task CreateExpense_ShouldRejectInvalidAmountString_WhenNotSupportedPositiveDecimal(string amount)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { date = "2026-07-02", amount });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task Latest_ShouldOrderOccurrenceThenIdentity_WhenInsertionOrderDiffersAndPurchasesRepeat()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.PersistExpenses = true;
        using var client = factory.CreateClient();
        var ids = new List<string>();
        foreach (var time in new[] { "18:00:00", "09:00:00", "18:00:00" })
        {
            var created = await client.PostAsJsonAsync("/Expense", new { date = "2026-10-10", occurredAt = $"2026-10-10T{time}+01:00", amount = 1m, description = "Duplicate", category = "Test" });
            ids.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }

        // Act
        var first = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/3")).GetProperty("expenses");
        var repeated = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/3")).GetProperty("expenses");

        // Assert
        var tied = new[] { ids[0], ids[2] }.OrderByDescending(id => id, StringComparer.Ordinal);
        Assert.Equal(tied.Append(ids[1]), first.EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        Assert.Equal(first.GetRawText(), repeated.GetRawText());
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Theory]
    [InlineData("USD", 1, HttpStatusCode.BadRequest)]
    [InlineData("EUR", 0.001, HttpStatusCode.OK)]
    [InlineData("EUR", 0, HttpStatusCode.BadRequest)]
    [InlineData("EUR", -1, HttpStatusCode.BadRequest)]
    public async Task CreateExpense_ShouldValidateCurrencyAndExactPositiveAmount_WhenPayloadReceived(string currency, decimal amount, HttpStatusCode expected)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { date = "2026-10-10", amount, currency, description = "Test", category = "Test" });

        // Assert
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest) Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task SmokeCleanup_ShouldDeleteOnlySmokeIdentity_WhenNormalIdentityRequested()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.PersistExpenses = true;
        using var normal = factory.CreateClient();
        using var smoke = factory.CreateClient();
        smoke.DefaultRequestHeaders.Add("X-Sheet-Scope", "smoke");
        var payload = new { date = "2026-10-10", amount = 1.01m, description = "Duplicate", category = "Test" };
        var normalId = (await (await normal.PostAsJsonAsync("/Expense", payload)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var smokeId = (await (await smoke.PostAsJsonAsync("/Expense", payload)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // Act
        var forbidden = await smoke.DeleteAsync($"/Smoke/{normalId}");
        var before = (await smoke.GetFromJsonAsync<JsonElement>("/Expense/latest/10")).GetProperty("expenses");
        var deleted = await normal.DeleteAsync($"/Smoke/{smokeId}");
        var after = (await smoke.GetFromJsonAsync<JsonElement>("/Expense/latest/10")).GetProperty("expenses");
        var personal = (await normal.GetFromJsonAsync<JsonElement>("/Expense/latest/10")).GetProperty("expenses");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Equal(smokeId, Assert.Single(before.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Empty(after.EnumerateArray());
        Assert.Equal(normalId, Assert.Single(personal.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await smoke.DeleteAsync($"/Smoke/{smokeId}")).StatusCode);
    }

    [Fact]
    public async Task CreateAndList_ShouldPreserveInstantDecimalAndIdentity_WhenTimestampSupplied()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.PersistExpenses = true;
        using var client = factory.CreateClient();

        // Act
        var created = await client.PostAsJsonAsync("/Expense", new
        {
            date = "2026-10-10", occurredAt = "2026-10-10T14:23:00.123456+01:00",
            amount = 123456789.123456789m, description = "Coffee", category = "Restaurants"
        });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
        var listed = JsonDocument.Parse(await client.GetStringAsync("/Expense/latest/10")).RootElement.GetProperty("expenses");

        // Assert
        var expense = Assert.Single(listed.EnumerateArray());
        Assert.Equal(id, expense.GetProperty("id").GetString());
        Assert.Equal(123456789.123456789m, expense.GetProperty("amount").GetDecimal());
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T14:23:00.123456+01:00"), expense.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(TimeSpan.FromHours(1), expense.GetProperty("occurredAt").GetDateTimeOffset().Offset);
        Assert.Equal("EUR", expense.GetProperty("currency").GetString());
        Assert.NotEqual(expense.GetProperty("occurredAt").GetDateTimeOffset(), expense.GetProperty("creationDate").GetDateTimeOffset());
        Assert.Equal(expense.GetProperty("creationDate").GetDateTimeOffset(), expense.GetProperty("updateDate").GetDateTimeOffset());
    }

    [Fact]
    public async Task CreateExpense_ShouldReturnOpaqueId_WhenPostgresSelected()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new
        {
            date = "2026-10-10", occurredAt = "2026-10-10T14:23:00+01:00",
            amount = 123456789.123456789m, description = "Coffee", category = "Restaurants"
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.String, body.GetProperty("id").ValueKind);
        Assert.True(Guid.TryParse(body.GetProperty("id").GetString(), out _));
        Assert.Empty(factory.SheetsHandler.AppendBodies);
    }
}
