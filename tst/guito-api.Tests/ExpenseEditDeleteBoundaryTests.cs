using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace GuitoApi.Tests;

public class ExpenseEditDeleteBoundaryTests
{
    [Fact]
    public async Task Detail_ShouldReturnExactStoredValuesAndRevision_WhenIdExists()
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/Expense/7");
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag!.ToString());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(7, detail.GetProperty("id").GetInt32());
        Assert.Equal("-9007199254740993.1200", detail.GetProperty("amount").GetString());
        Assert.Equal("2026-07-02T14:23:45.123456+01:00", detail.GetProperty("bookingDate").GetString());
        Assert.Equal("human@example.com", detail.GetProperty("creatorEmail").GetString());
        Assert.Equal("1", detail.GetProperty("revision").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("updateDate").ValueKind);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    [Fact]
    public async Task Update_ShouldAdvanceRevisionAndPreserveIdentityAuditAndPrecision_WhenLoadedRevisionMatches()
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var before = await client.GetFromJsonAsync<JsonElement>("/Expense/7");
        using var request = UpdateRequest("7", "\"1\"", "Corrected purchase");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("\"2\"", response.Headers.ETag!.ToString());
        Assert.Equal("2", after.GetProperty("revision").GetString());
        foreach (var field in new[] { "id", "amount", "bookingDate", "creatorEmail", "creationDate" })
            Assert.Equal(before.GetProperty(field).GetRawText(), after.GetProperty(field).GetRawText());
        Assert.Equal("Corrected purchase", after.GetProperty("description").GetString());
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("updateDate").ValueKind);
        Assert.Equal("Original purchase", (await client.GetFromJsonAsync<JsonElement>("/Expense/8")).GetProperty("description").GetString());
    }

    [Fact]
    public async Task Delete_ShouldRemoveOnlyTargetAndUnlinkItsTransactions_WhenLoadedRevisionMatches()
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = DeleteRequest("7", "\"1\"");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Expense/7")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Expense/8")).StatusCode);
        Assert.Contains(factory.Postgres.ExecutedSql, sql => sql.StartsWith("UPDATE bank_transactions", StringComparison.Ordinal));
        Assert.Equal(1, factory.Postgres.Commits);
        Assert.DoesNotContain(factory.Postgres.ExecutedSql, sql => sql.StartsWith("DELETE FROM bank_transactions", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("PUT", null, 428)]
    [InlineData("DELETE", null, 428)]
    [InlineData("PUT", "1", 400)]
    [InlineData("DELETE", "1", 400)]
    [InlineData("PUT", "W/\"1\"", 400)]
    [InlineData("DELETE", "*", 400)]
    [InlineData("PUT", "\"1\", \"2\"", 400)]
    [InlineData("DELETE", "\"01\"", 400)]
    [InlineData("PUT", "\"0\"", 400)]
    [InlineData("PUT", "\"9223372036854775808\"", 400)]
    [InlineData("DELETE", "\"2\"", 412)]
    [InlineData("PUT", "\"2\"", 412)]
    public async Task Mutation_ShouldRejectWithoutChangingExpense_WhenRevisionIsMissingMalformedOrStale(string method, string? revision, int status)
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var before = await client.GetStringAsync("/Expense/7");
        factory.Postgres.ExecutedSql.Clear();
        using var request = method == "PUT" ? UpdateRequest("7", revision) : DeleteRequest("7", revision);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        if (status != 412) Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Equal(before, await client.GetStringAsync("/Expense/7"));
        Assert.Equal(0, factory.Postgres.Commits);
    }

    [Theory]
    [InlineData("GET", "no-id", 400)]
    [InlineData("PUT", "2147483648", 400)]
    [InlineData("DELETE", "2147483648", 400)]
    [InlineData("GET", "2147483648", 400)]
    [InlineData("PUT", "no-id", 400)]
    [InlineData("DELETE", "no-id", 400)]
    [InlineData("GET", "999", 404)]
    [InlineData("PUT", "999", 404)]
    [InlineData("DELETE", "999", 404)]
    public async Task Expense_ShouldReturnBindingOrNotFoundError_WhenIdIsInvalidOrAbsent(string method, string id, int status)
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = method == "PUT" ? UpdateRequest(id, "\"1\"") : method == "DELETE" ? DeleteRequest(id, "\"1\"") : new(HttpMethod.Get, $"/Expense/{id}");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        if (status == 400) Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Equal(0, factory.Postgres.Commits);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Mutation_ShouldReturnNotFound_WhenExpenseWasAlreadyDeleted(string method)
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var delete = DeleteRequest("7", "\"1\"");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);
        using var retry = method == "PUT" ? UpdateRequest("7", "\"1\"") : DeleteRequest("7", "\"1\"");

        // Act
        var response = await client.SendAsync(retry);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, factory.Postgres.Commits);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Mutation_ShouldRejectOldRevision_WhenAnotherUpdateCommitted(string method)
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var first = UpdateRequest("7", "\"1\"", "First writer");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(first)).StatusCode);
        using var stale = method == "PUT" ? UpdateRequest("7", "\"1\"", "Stale writer") : DeleteRequest("7", "\"1\"");

        // Act
        var response = await client.SendAsync(stale);

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var detail = await client.GetFromJsonAsync<JsonElement>("/Expense/7");
        Assert.Equal("First writer", detail.GetProperty("description").GetString());
        Assert.Equal("2", detail.GetProperty("revision").GetString());
        Assert.Equal(1, factory.Postgres.Commits);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Mutation_ShouldPermitOnlyOneWinner_WhenConcurrentOperationsShareRevision(string secondMethod)
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var first = UpdateRequest("7", "\"1\"", "Winner A");
        using var second = secondMethod == "PUT" ? UpdateRequest("7", "\"1\"", "Winner B") : DeleteRequest("7", "\"1\"");

        // Act
        var responses = await Task.WhenAll(client.SendAsync(first), client.SendAsync(second));

        // Assert
        Assert.Single(responses, r => r.IsSuccessStatusCode);
        Assert.Single(responses, r => r.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound);
        Assert.Equal(1, factory.Postgres.Commits);
        Assert.Contains(factory.Postgres.ExecutedSql, sql => sql.Contains("WHERE id = :id FOR UPDATE", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Expense/8")).StatusCode);
    }

    [Theory]
    [InlineData("GET", "Sheets", false)]
    [InlineData("PUT", "Sheets", false)]
    [InlineData("DELETE", "Sheets", false)]
    [InlineData("GET", "Postgres", true)]
    [InlineData("PUT", "Postgres", true)]
    [InlineData("DELETE", "Postgres", true)]
    public async Task Expense_ShouldRejectWithoutDatastoreAccess_WhenStoreOrScopeIsUnavailable(string method, string store, bool smoke)
    {
        // Arrange
        using var factory = CreateFactory();
        factory.ExpenseStore = store;
        using var client = factory.CreateClient();
        if (smoke) client.DefaultRequestHeaders.Add("X-Sheet-Scope", "smoke");
        using var request = method == "PUT" ? UpdateRequest("7", "\"1\"") : method == "DELETE" ? DeleteRequest("7", "\"1\"") : new(HttpMethod.Get, "/Expense/7");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
        Assert.Empty(factory.SheetsHandler.DeleteDimensions);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Expense/latest/1")).StatusCode);
    }

    [Fact]
    public async Task Detail_ShouldExposeEtagToBrowser_WhenCrossOriginRequestUsesAllowedOrigin()
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "https://ui.example.test");

        // Act
        var response = await client.GetAsync("/Expense/7");

        // Assert
        Assert.Contains("ETag", string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers")), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_ShouldRetainTransactionAndResetOnlyTargetLink_WhenLinkSchemaIsSupported(bool reviewSchema)
    {
        // Arrange
        using var factory = CreateFactory();
        factory.Postgres.HasBankReviewSchema = reviewSchema;
        factory.Postgres.LinkedTransactions.Add(100, (7, "matched"));
        factory.Postgres.LinkedTransactions.Add(101, (8, "matched"));
        factory.Postgres.LinkedTransactions.Add(102, (null, "ignored"));
        using var client = factory.CreateClient();
        using var request = DeleteRequest("7", "\"1\"");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(factory.Postgres.LinkedTransactions[100].ExpenseId);
        if (reviewSchema) Assert.Equal("needsReview", factory.Postgres.LinkedTransactions[100].Status);
        Assert.Equal((8, "matched"), factory.Postgres.LinkedTransactions[101]);
        Assert.Equal(((int?)null, "ignored"), factory.Postgres.LinkedTransactions[102]);
        Assert.Equal(3, factory.Postgres.LinkedTransactions.Count);
    }

    [Theory]
    [InlineData("description", null)]
    [InlineData("category", null)]
    [InlineData("amount", null)]
    [InlineData("bookingDate", null)]
    [InlineData("description", "null")]
    [InlineData("category", "null")]
    [InlineData("amount", "null")]
    [InlineData("bookingDate", "null")]
    [InlineData("amount", "\"0.00000000000000000000000000001\"")]
    [InlineData("amount", "\"79228162514264337593543950336\"")]
    [InlineData("amount", "\"1.00000000000000000000000000001\"")]
    [InlineData("bookingDate", "\"2026-03-29T01:30:00\"")]
    [InlineData("bookingDate", "\"2026-10-25T01:30:00\"")]
    [InlineData("bookingDate", "\"2026-07-02T14:23:45.1234567+01:00\"")]
    [InlineData("currency", "\"USD\"")]
    [InlineData("date", "null")]
    [InlineData("OccurredAt", "null")]
    public async Task Update_ShouldRejectBeforeStorage_WhenRequiredFieldsOrExactValuesAreInvalid(string property, string? rawValue)
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var fields = new Dictionary<string, JsonElement>
        {
            ["description"] = JsonSerializer.SerializeToElement("Updated"),
            ["category"] = JsonSerializer.SerializeToElement("Groceries"),
            ["amount"] = JsonSerializer.SerializeToElement("-9007199254740993.1200"),
            ["bookingDate"] = JsonSerializer.SerializeToElement("2026-07-02T14:23:45.123456+01:00")
        };
        if (rawValue is null) fields.Remove(property);
        else fields[property] = JsonSerializer.Deserialize<JsonElement>(rawValue);
        using var request = UpdateRequest("7", "\"1\"");
        request.Content = JsonContent.Create(fields);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Equal(0, factory.Postgres.Commits);
    }

    [Theory]
    [InlineData("0", "2026-01-02", "2026-01-02T00:00:00+00:00")]
    [InlineData("79228162514264337593543950335", "2026-07-02", "2026-07-02T00:00:00+01:00")]
    [InlineData("-0.0000000000000000000000000001", "2026-10-25T01:30:00Z", "2026-10-25T01:30:00+00:00")]
    public async Task Update_ShouldReplaceEditableValuesExactly_WhenValidAmountAndResolvedOccurrenceAreSubmitted(string amount, string bookingDate, string expectedDate)
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        factory.Postgres.HasBankReviewSchema = true;
        factory.Postgres.LinkedTransactions.Add(100, (7, "matched"));
        using var request = UpdateRequest("7", "\"1\"");
        request.Content = JsonContent.Create(new { description = "", category = "Restaurants", amount, bookingDate });

        // Act
        var response = await client.SendAsync(request);
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(amount, detail.GetProperty("amount").GetString());
        Assert.Equal(expectedDate, detail.GetProperty("bookingDate").GetString());
        Assert.Equal("Restaurants", detail.GetProperty("category").GetString());
        Assert.Equal("", detail.GetProperty("description").GetString());
        Assert.Equal((7, "matched"), factory.Postgres.LinkedTransactions[100]);
    }

    [Fact]
    public async Task Update_ShouldRejectUnknownCategoryWithoutChanges_WhenCategoryIsNotAnExactMirrorName()
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var before = await client.GetStringAsync("/Expense/7");
        using var request = UpdateRequest("7", "\"1\"");
        request.Content = JsonContent.Create(new { description = "Updated", category = "groceries", amount = "0", bookingDate = "2026-01-02" });

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(before, await client.GetStringAsync("/Expense/7"));
        Assert.Equal(0, factory.Postgres.Commits);
        Assert.Equal(1, factory.Postgres.Rollbacks);
    }

    [Fact]
    public async Task Update_ShouldAdvanceRevision_WhenEveryEditableValueIsUnchanged()
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = UpdateRequest("7", "\"1\"", "Original purchase");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag!.ToString());
        using var stale = DeleteRequest("7", "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);
    }

    [Theory]
    [InlineData("PUT", 4)]
    [InlineData("DELETE", 5)]
    public async Task Mutation_ShouldRollbackAllChanges_WhenProviderFailsAfterWrite(string method, int failAt)
    {
        // Arrange
        using var factory = CreateFactory();
        factory.Postgres.HasBankReviewSchema = true;
        factory.Postgres.LinkedTransactions.Add(100, (7, "matched"));
        using var client = factory.CreateClient();
        var before = await client.GetStringAsync("/Expense/7");
        factory.Postgres.ExecutedSql.Clear();
        factory.Postgres.ExecuteException = new InvalidOperationException("Transport failed after mutation.");
        factory.Postgres.ExecuteExceptionAtCall = failAt;
        using var request = method == "PUT" ? UpdateRequest("7", "\"1\"") : DeleteRequest("7", "\"1\"");

        // Act
        var response = await client.SendAsync(request);
        factory.Postgres.ExecuteException = null;

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, await client.GetStringAsync("/Expense/7"));
        Assert.Equal((7, "matched"), factory.Postgres.LinkedTransactions[100]);
        Assert.Equal(0, factory.Postgres.Commits);
        Assert.Equal(1, factory.Postgres.Rollbacks);
    }

    [Fact]
    public async Task Reads_ShouldReflectUpdateAndDeletion_WhenMutationsCommit()
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var update = UpdateRequest("7", "\"1\"", "Updated in history");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(update)).StatusCode);

        // Act
        var latest = await client.GetStringAsync("/Expense/latest/10");
        var history = await client.GetStringAsync("/Expense/history");
        using var delete = DeleteRequest("7", "\"2\"");
        var deleted = await client.SendAsync(delete);

        // Assert
        Assert.Contains("Updated in history", latest);
        Assert.Contains("Updated in history", history);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.DoesNotContain("Updated in history", await client.GetStringAsync("/Expense/latest/10"));
        Assert.DoesNotContain("Updated in history", await client.GetStringAsync("/Expense/history"));
    }

    [Theory]
    [InlineData("GET", false, 401)]
    [InlineData("PUT", false, 401)]
    [InlineData("DELETE", false, 401)]
    [InlineData("GET", true, 200)]
    [InlineData("PUT", true, 200)]
    [InlineData("DELETE", true, 204)]
    public async Task Expense_ShouldEnforceExistingAuthGates_WhenAgentCredentialsAreRequired(string method, bool authenticated, int status)
    {
        // Arrange
        using var factory = CreateFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(config, new Dictionary<string, string?>
            {
                ["AppConfiguration:Authentication:ValidateApiKey"] = "true",
                ["AppConfiguration:Authentication:ValidateIdToken"] = "true"
            })));
        using var client = host.CreateClient();
        if (authenticated) client.DefaultRequestHeaders.Add("X-Api-Key", "test-agent-key");
        using var request = method == "PUT" ? UpdateRequest("7", "\"1\"") : method == "DELETE" ? DeleteRequest("7", "\"1\"") : new(HttpMethod.Get, "/Expense/7");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        if (!authenticated) Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public async Task Mutation_ShouldAllowBrowserPreflight_WhenIfMatchAndAuthorizationAreRequested(string environment)
    {
        // Arrange
        using var factory = CreateFactory();
        factory.Environment = environment;
        factory.UiOrigin = "https://ui.example.test";
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/Expense/7");
        request.Headers.Add("Origin", factory.UiOrigin);
        request.Headers.Add("Access-Control-Request-Method", "PUT");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,if-match");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("PUT", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")));
        Assert.Contains("if-match", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    private static HttpRequestMessage DeleteRequest(string id, string? revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/Expense/{id}");
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision);
        return request;
    }

    private static HttpRequestMessage UpdateRequest(string id, string? revision, string description = "Updated")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/Expense/{id}");
        request.Content = JsonContent.Create(new { description, category = "Groceries", amount = "-9007199254740993.1200", bookingDate = "2026-07-02T14:23:45.123456+01:00" });
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision);
        return request;
    }

    private static CustomWebApplicationFactory CreateFactory()
    {
        var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        factory.Postgres.NextTransactionId = "expense-test-transaction";
        factory.Postgres.SeedExpense(7, "2026-07-02T13:23:45.123456Z", "-9007199254740993.1200", "Groceries");
        factory.Postgres.SeedExpense(8, "2026-07-02T13:23:45.123456Z", "-9007199254740993.1200", "Groceries");
        return factory;
    }
}
