using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.Infrastructure.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using GuitoApi.Configuration;

namespace GuitoApi.Tests;

public class PostgresExpenseBoundaryTests
{
    [Fact]
    public async Task CreateExpense_ShouldBindOffsetAwareDate_WhenControllerReceivesTimestamp()
    {
        // Arrange — inspect the agreed HTTP/service seam, not converter internals.
        using var factory = new CustomWebApplicationFactory();
        var service = new CapturingCreateService();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<GuitoApi.Services.Expense.ICreateExpenseService>(service)));
        using var client = host.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { date = "2026-07-01T23:30:00.123456Z", amount = "-1.23" });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var date = Assert.IsType<DateTimeOffset>(service.Date);
        Assert.Equal(DateTimeOffset.Parse("2026-07-02T00:30:00.123456+01:00"), date);
        Assert.Equal(TimeSpan.FromHours(1), date.Offset);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateExpense_ShouldRefuseOutOfRangeIdentity_WhenDataApiReturnsBeyondInt32(bool textId)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.NextResults.Enqueue(new PostgresResult([], [[PostgresValue.FromLong(2)]], 0));
        factory.Postgres.NextResults.Enqueue(new PostgresResult([], [[textId
            ? PostgresValue.FromString("2147483648") : PostgresValue.FromLong(2147483648L)]], 1));
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { date = "2026-07-02", amount = 1, category = "Test" });

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("\"id\"", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.SheetsHandler.AppendBodies);
    }

    [Fact]
    public async Task Latest_ShouldRetainNullsAndSingleFields_WhenLegacySheetsCellsAreMalformed()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        factory.SheetsHandler.LatestValuesJson = """
            [["bad date", "", "", "bad amount", "Legacy", "Test", "user@example.test"]]
            """;
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/Expense/latest/1");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var expense = Assert.Single(body.GetProperty("expenses").EnumerateArray());
        Assert.Equal(JsonValueKind.Number, expense.GetProperty("id").ValueKind);
        foreach (var field in new[] { "bookingDate", "amount", "creationDate", "updateDate" })
            Assert.Equal(JsonValueKind.Null, expense.GetProperty(field).ValueKind);
        foreach (var removed in new[] { "date", "occurredAt", "amountExact", "storedOrder" })
            Assert.False(expense.TryGetProperty(removed, out _));
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.AppendBodies);
    }

    private sealed class CapturingCreateService : GuitoApi.Services.Expense.ICreateExpenseService
    {
        public object? Date { get; private set; }
        public Task<GuitoApi.DataTransferObjects.Output.ExpenseCreated> CreateAsync(GuitoApi.DataTransferObjects.Input.ExpenseCreate value, CancellationToken cancellationToken = default)
        {
            Date = value.Date;
            return Task.FromResult(new GuitoApi.DataTransferObjects.Output.ExpenseCreated { Id = 1 });
        }
    }

    [Theory]
    [InlineData("occurredAt", "2026-07-02T14:00:00+01:00", HttpStatusCode.BadRequest)]
    [InlineData("OccurredAt", null, HttpStatusCode.BadRequest)]
    [InlineData("OCCURREDAT", "garbage", HttpStatusCode.BadRequest)]
    [InlineData("futureMetadata", "ignored", HttpStatusCode.OK)]
    public async Task CreateExpense_ShouldRejectRemovedOccurrencePropertyOnly_WhenUnmappedInputReceived(string property, string? value, HttpStatusCode expected)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();
        var payload = new Dictionary<string, object?> { ["date"] = "2026-07-02", ["amount"] = 1m, ["category"] = "Test", [property] = value };

        // Act
        var response = await client.PostAsJsonAsync("/Expense", payload);

        // Assert
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest) Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.AppendBodies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateExpense_ShouldReturnIntegerIdentity_WhenDataApiEncodesIdsAsTextOrLong(bool textIds)
    {
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.NextResults.Enqueue(new PostgresResult([], [[textIds
            ? PostgresValue.FromString("2147483648") : PostgresValue.FromLong(2147483648L)]], 0));
        factory.Postgres.NextResults.Enqueue(new PostgresResult([], [[textIds
            ? PostgresValue.FromString("2147483647") : PostgresValue.FromLong(2147483647L)]], 1));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/Expense", new { date = "2026-10-10", amount = "-1.25", category = "Test" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2147483647, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32());
        var categoryParameter = Assert.Single(factory.Postgres.ExecutedParameters[1]!, p => p.Name == "category_id");
        Assert.Equal(2147483648L, categoryParameter.Value.LongValue);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Latest_ShouldMapPostgresTextValues_WhenDataApiReturnsUtcInstantsAndNumericStrings(bool textId)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        factory.Postgres.NextResults.Enqueue(new PostgresResult([], [[
            textId ? PostgresValue.FromString("2147483647") : PostgresValue.FromLong(2147483647),
            PostgresValue.FromString("2026-07-09 23:00:00+00"),
            PostgresValue.FromString("79228162514264337593543950335"),
            PostgresValue.FromString("Unchanged"),
            PostgresValue.FromString("Groceries"), PostgresValue.FromString("jose@example.test"),
            PostgresValue.FromString("2026-10-10 20:00:00.123456+00"),
            PostgresValue.FromString("2026-10-10 20:00:00.123456+00")]], 0));
        using var client = factory.CreateClient();

        // Act
        var latest = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1")).GetProperty("expenses");

        // Assert
        var expense = Assert.Single(latest.EnumerateArray());
        Assert.Equal(decimal.MaxValue, decimal.Parse(expense.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(2147483647, expense.GetProperty("id").GetInt32());
        Assert.Equal("Groceries", expense.GetProperty("category").GetString());
        Assert.Equal("EUR", expense.GetProperty("currency").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T21:00:00.123456+01:00"), expense.GetProperty("updateDate").GetDateTimeOffset());
        Assert.Equal("2026-07-10T00:00:00+01:00", expense.GetProperty("bookingDate").GetDateTimeOffset().ToString("yyyy-MM-ddTHH:mm:sszzz"));
        Assert.False(expense.TryGetProperty("date", out _));
        Assert.False(expense.TryGetProperty("occurredAt", out _));
        Assert.Equal("jose@example.test", expense.GetProperty("creatorEmail").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T21:00:00.123456+01:00"), expense.GetProperty("creationDate").GetDateTimeOffset());
        Assert.False(expense.TryGetProperty("storedOrder", out _));
    }

    [Theory]
    [InlineData("POST", "/Expense", false)]
    [InlineData("GET", "/Expense/latest/1", false)]
    [InlineData("DELETE", "/Smoke/opaque-not-a-number", false)]
    [InlineData("POST", "/Expense", true)]
    [InlineData("GET", "/Expense/latest/1", true)]
    [InlineData("DELETE", "/Smoke/100", true)]
    public async Task ExpenseEndpoints_ShouldRejectUnauthenticatedRequests_WhenPostgresSelected(string method, string path, bool smoke)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var secured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AppConfiguration:Authentication:ValidateApiKey"] = "true" })));
        using var client = secured.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (smoke) request.Headers.Add("X-Sheet-Scope", "smoke");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.AppendBodies);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
        Assert.Empty(factory.SheetsHandler.DeleteDimensions);
    }

    [Fact]
    public async Task CreateExpense_ShouldSurfaceFailureWithoutSuccessIdentity_WhenDataApiFails()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
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
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
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
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
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
    [InlineData("2026-07-02T00:30:00.123456+10:00", "2026-07-01T15:30:00.123456+01:00")]
    [InlineData("2026-07-01T23:30:00.123456-10:00", "2026-07-02T10:30:00.123456+01:00")]
    public async Task CreateAndList_ShouldNormalizeKnownInstant_WhenInputOffsetIsNotLisbon(string occurredAt, string expected)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var created = await client.PostAsJsonAsync("/Expense", new { date = occurredAt, amount = 1m, category = "Test" });

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var latest = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1")).GetProperty("expenses");
        var expense = Assert.Single(latest.EnumerateArray());
        Assert.Equal(DateTimeOffset.Parse(occurredAt).UtcTicks, expense.GetProperty("bookingDate").GetDateTimeOffset().UtcTicks);
        Assert.Equal(DateTimeOffset.Parse(expected).ToString("O"), expense.GetProperty("bookingDate").GetDateTimeOffset().ToString("O"));
        Assert.Equal(DateTimeOffset.Parse(expected).Date, expense.GetProperty("bookingDate").GetDateTimeOffset().Date);

        // Latest-prefill compatibility: submit the unchanged output instant through the sole input date.
        var resubmitted = await client.PostAsJsonAsync("/Expense", new
        {
            date = expense.GetProperty("bookingDate").GetString(),
            amount = expense.GetProperty("amount").GetString(), category = expense.GetProperty("category").GetString()
        });
        Assert.Equal(HttpStatusCode.OK, resubmitted.StatusCode);
        var repeated = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1")).GetProperty("expenses")[0];
        Assert.Equal(expense.GetProperty("bookingDate").GetString(), repeated.GetProperty("bookingDate").GetString());
        Assert.Equal(expense.GetProperty("amount").GetString(), repeated.GetProperty("amount").GetString());
    }

    [Theory]
    [InlineData("2026-03-29T01:30:00+00:00", HttpStatusCode.OK)]
    [InlineData("2026-03-29T01:30:00+01:00", HttpStatusCode.OK)]
    [InlineData("2026-10-25T01:30:00+01:00", HttpStatusCode.OK)]
    [InlineData("2026-10-25T01:30:00+00:00", HttpStatusCode.OK)]
    [InlineData("2026-10-10T14:00:00Z", HttpStatusCode.OK)]
    [InlineData("2026-10-10T14:00:00", HttpStatusCode.BadRequest)]
    [InlineData("2026-10-11T14:00:00+01:00", HttpStatusCode.OK)]
    [InlineData("2026-10-10T14:00:00.1234567+01:00", HttpStatusCode.BadRequest)]
    [InlineData("garbage", HttpStatusCode.BadRequest)]
    [InlineData("2026-07-01T23:30:00.123456Z", HttpStatusCode.OK)]
    [InlineData("2026-03-29T01:30:00", HttpStatusCode.BadRequest)]
    [InlineData("2026-10-25T01:30:00", HttpStatusCode.BadRequest)]
    [InlineData("2026-10-10T14:00:00.1234560+01:00", HttpStatusCode.BadRequest)]
    [InlineData("2026-10-10T14:00:00.12345678+01:00", HttpStatusCode.BadRequest)]
    [InlineData("2026-02-30", HttpStatusCode.BadRequest)]
    [InlineData("2026-1-10", HttpStatusCode.BadRequest)]
    [InlineData("", HttpStatusCode.BadRequest)]
    [InlineData(null, HttpStatusCode.BadRequest)]
    public async Task CreateExpense_ShouldValidateOffsetAndPrecision_WhenDateContainsExplicitInstant(string? date, HttpStatusCode expected)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { date, amount = 1m, description = "Test", category = "Test" });

        // Assert
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK) Assert.Empty(factory.Postgres.ExecutedSql);
        else
        {
            var listed = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/10")).GetProperty("expenses");
            Assert.Equal(DateTimeOffset.Parse(date!), Assert.Single(listed.EnumerateArray()).GetProperty("bookingDate").GetDateTimeOffset());
        }
    }

    [Theory]
    [InlineData("2026-01-10", "2026-01-10T00:00:00+00:00")]
    [InlineData("2026-07-10", "2026-07-10T00:00:00+01:00")]
    public async Task CreateAndList_ShouldUseLisbonMidnight_WhenLegacyDateHasNoTime(string date, string expected)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var created = await client.PostAsJsonAsync("/Expense", new { date, amount = 0.0000000000000000000000000001m, description = "Original description", category = "Original category" });
        var listed = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/10")).GetProperty("expenses");

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var expense = Assert.Single(listed.EnumerateArray());
        Assert.Equal(expected, expense.GetProperty("bookingDate").GetDateTimeOffset().ToString("yyyy-MM-ddTHH:mm:sszzz"));
        Assert.Equal(0.0000000000000000000000000001m, decimal.Parse(expense.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Original description", expense.GetProperty("description").GetString());
        Assert.Equal("Original category", expense.GetProperty("category").GetString());
        Assert.Equal("xunga.meireles@gmail.com", expense.GetProperty("creatorEmail").GetString());
    }

    [Theory]
    [InlineData("123456789.1234567890123456789")]
    [InlineData("0.0000000000000000000000000001")]
    [InlineData("0")]
    [InlineData("-123456789.1234567890123456789")]
    [InlineData("-79228162514264337593543950335")]
    public async Task CreateAndList_ShouldPreserveExactAmountText_WhenDecimalStringExceedsJavascriptPrecision(string amount)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var created = await client.PostAsJsonAsync("/Expense", new { date = "2026-07-02", amount, category = "Test" });

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var latest = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1")).GetProperty("expenses");
        var expense = Assert.Single(latest.EnumerateArray());
        Assert.Equal(JsonValueKind.String, expense.GetProperty("amount").ValueKind);
        Assert.Equal(amount, expense.GetProperty("amount").GetString());
        Assert.False(expense.TryGetProperty("amountExact", out _));
    }

    [Theory]
    [InlineData("\"-79228162514264337593543950336\"")]
    [InlineData("-79228162514264337593543950336")]
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
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();
        using var content = new StringContent($"{{\"date\":\"2026-07-02\",\"category\":\"Test\",\"amount\":{amountToken}}}", System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/Expense", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData("\"-0\"", "0")]
    [InlineData("-0", "0")]
    [InlineData("\"+0\"", "0")]
    [InlineData("-0e-999999999999999999999", "0")]
    [InlineData("\"-79228162514264337593543950335\"", "-79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335", "-79228162514264337593543950335")]
    [InlineData("9007199254740993", "9007199254740993")]
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
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();
        using var content = new StringContent($"{{\"date\":\"2026-07-02\",\"category\":\"Test\",\"amount\":{amountToken}}}", System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/Expense", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var latest = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1")).GetProperty("expenses");
        var expense = Assert.Single(latest.EnumerateArray());
        Assert.Equal(expected, expense.GetProperty("amount").GetString());
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(expense.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAndList_ShouldPreserveExactValue_WhenAmountTokenSpansRequestBuffers(bool quoted)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();
        var amount = $"1.23{new string('0', 20000)}";
        var token = quoted ? JsonSerializer.Serialize(amount) : amount;
        using var content = new StringContent($"{{\"date\":\"2026-07-02\",\"category\":\"Test\",\"amount\":{token}}}", System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/Expense", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var latest = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1")).GetProperty("expenses");
        Assert.Equal("1.23", Assert.Single(latest.EnumerateArray()).GetProperty("amount").GetString());
    }

    [Theory]
    [InlineData("1,23")]
    [InlineData("NaN")]
    [InlineData("79228162514264337593543950336")]
    public async Task CreateExpense_ShouldRejectInvalidAmountString_WhenNotSupportedDecimal(string amount)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { date = "2026-07-02", amount, category = "Test" });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task Latest_ShouldOrderOccurrenceThenIdentity_WhenInsertionOrderDiffersAndPurchasesRepeat()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();
        var ids = new List<int>();
        foreach (var time in new[] { "18:00:00", "09:00:00", "18:00:00" })
        {
            var created = await client.PostAsJsonAsync("/Expense", new { date = $"2026-10-10T{time}+01:00", amount = 1m, description = "Duplicate", category = "Test" });
            ids.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32());
        }

        // Act
        var first = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/3")).GetProperty("expenses");
        var repeated = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/3")).GetProperty("expenses");

        // Assert
        var tied = new[] { ids[0], ids[2] }.OrderByDescending(id => id);
        Assert.Equal(tied.Append(ids[1]), first.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
        Assert.Equal(first.GetRawText(), repeated.GetRawText());
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Theory]
    [InlineData("USD", 1, HttpStatusCode.BadRequest)]
    [InlineData("EUR", 0.001, HttpStatusCode.OK)]
    [InlineData("EUR", 0, HttpStatusCode.OK)]
    [InlineData("EUR", -1, HttpStatusCode.OK)]
    public async Task CreateExpense_ShouldValidateCurrencyAndExactAmount_WhenPayloadReceived(string currency, decimal amount, HttpStatusCode expected)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { date = "2026-10-10", amount, currency, description = "Test", category = "Test" });

        // Assert
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest) Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SmokeCleanup_ShouldUseOnlySheets_WhenPostgresAuthoritySelected(bool deleteHeader)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        using var smoke = factory.CreateClient();
        using var cleanup = factory.CreateClient();
        smoke.DefaultRequestHeaders.Add("X-Sheet-Scope", "smoke");
        if (deleteHeader) cleanup.DefaultRequestHeaders.Add("X-Sheet-Scope", "smoke");
        var payload = new { date = "2026-10-10", amount = 1.01m, description = "Smoke", category = "Test" };

        // Act
        var created = await smoke.PostAsJsonAsync("/Expense", payload);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var listed = await smoke.GetAsync("/Expense/latest/10");
        var deleted = await cleanup.DeleteAsync($"/Smoke/{id}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(FakeSheetsHttpHandler.NextRowIndex, id);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Equal(factory.Services.GetRequiredService<IOptions<AppConfigurationOptions>>().Value.Googlesheets.ExpensesSmokeRange, Assert.Single(factory.SheetsHandler.AppendRanges));
        Assert.All(factory.SheetsHandler.ReadRanges, range => Assert.StartsWith(factory.Services.GetRequiredService<IOptions<AppConfigurationOptions>>().Value.Googlesheets.ExpensesSmokeRange.Split('!')[0] + "!", range));
        Assert.Equal(FakeSheetsHttpHandler.SmokeSheetId, Assert.Single(factory.SheetsHandler.DeleteDimensions).SheetId);
    }

    [Fact]
    public async Task SmokeCleanup_ShouldLeavePersonalExpenseUnchanged_WhenPostgresIdIsSubmittedWithoutHeader()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/Expense", new { date = "2026-10-10", amount = 1m, category = "Test" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        factory.Postgres.ExecutedSql.Clear();

        // Act
        var deleted = await client.DeleteAsync($"/Smoke/{id}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        var expenses = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/10")).GetProperty("expenses");
        Assert.Equal(id, Assert.Single(expenses.EnumerateArray()).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task CreateAndList_ShouldPreserveInstantDecimalAndIdentity_WhenTimestampSupplied()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var created = await client.PostAsJsonAsync("/Expense", new
        {
            date = "2026-10-10T14:23:00.123456+01:00",
            amount = 123456789.123456789m, description = "Coffee", category = "Restaurants"
        });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        var listed = JsonDocument.Parse(await client.GetStringAsync("/Expense/latest/10")).RootElement.GetProperty("expenses");

        // Assert
        var expense = Assert.Single(listed.EnumerateArray());
        Assert.Equal(id, expense.GetProperty("id").GetInt32());
        Assert.Equal(123456789.123456789m, decimal.Parse(expense.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T14:23:00.123456+01:00"), expense.GetProperty("bookingDate").GetDateTimeOffset());
        Assert.Equal(TimeSpan.FromHours(1), expense.GetProperty("bookingDate").GetDateTimeOffset().Offset);
        Assert.Equal("EUR", expense.GetProperty("currency").GetString());
        Assert.NotEqual(expense.GetProperty("bookingDate").GetDateTimeOffset(), expense.GetProperty("creationDate").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, expense.GetProperty("updateDate").ValueKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("test")]
    [InlineData(" Test ")]
    [InlineData("Unseeded")]
    public async Task CreateExpense_ShouldReturnUnprocessableEntity_WhenCategoryHasNoExactMirrorMatch(string category)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { date = "2026-10-10", amount = 1m, category });

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain("\"id\"", await response.Content.ReadAsStringAsync());
        Assert.Equal("SELECT id FROM categories WHERE name = :name", Assert.Single(factory.Postgres.ExecutedSql));
        Assert.Empty(factory.SheetsHandler.AppendBodies);
    }

    [Fact]
    public async Task CreateExpense_ShouldBindCategoryAsInt64AndAmountAsText_WhenMirrorIdentityExceedsInt32()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new { date = "2026-10-10", amount = "-123456789.1234567890123456789", category = "Test" });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parameters = factory.Postgres.ExecutedParameters[1]!.ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(2147483648L, parameters["category_id"].LongValue);
        Assert.Equal("-123456789.1234567890123456789", parameters["amount"].StringValue);
        Assert.Null(parameters["amount"].DoubleValue);
        Assert.Equal(new[] { "amount", "booking_date", "category_id", "creator_email", "description" }, parameters.Keys.Order());
        Assert.Contains("RETURNING id", factory.Postgres.ExecutedSql[1]);
    }

    [Fact]
    public async Task Latest_ShouldOrderNumericIdentities_WhenMoreThanNineExpensesShareBookingDate()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();
        for (var i = 0; i < 12; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/Expense", new { date = "2026-10-10", amount = 1m, category = "Test" })).StatusCode);

        // Act
        var expenses = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/3")).GetProperty("expenses");

        // Assert
        Assert.Equal(new[] { 12, 11, 10 }, expenses.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
        Assert.Contains("ORDER BY e.booking_date DESC, e.id DESC", factory.Postgres.ExecutedSql.Last());
    }

    [Fact]
    public async Task CreateExpense_ShouldReturnIntegerId_WhenPostgresSelected()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.NextResults.Enqueue(new PostgresResult([], [[PostgresValue.FromLong(2)]], 0));
        factory.Postgres.NextResults.Enqueue(new PostgresResult([], [[PostgresValue.FromLong(2147483647)]], 1));
        using var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/Expense", new
        {
            date = "2026-10-10T14:23:00+01:00",
            amount = 123456789.123456789m, description = "Coffee", category = "Restaurants"
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Number, body.GetProperty("id").ValueKind);
        Assert.Equal(2147483647, body.GetProperty("id").GetInt32());
        Assert.Empty(factory.SheetsHandler.AppendBodies);
    }
}
