using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using GuitoApi.Infrastructure.Postgres;

namespace GuitoApi.Tests;

public class BankIdentityBoundaryTests
{
    [Fact]
    public async Task Sync_ShouldRefreshOneMovement_WhenReliableReferenceHasCorrectedBookingDate()
    {
        // Arrange
        using var factory = Factory();
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference", "2026-10-01", "12.3400")));
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference", "2026-10-02", "12.34", "Corrected merchant")));
        using var client = factory.CreateClient();
        await SyncAsync(client, 1);

        // Act
        await SyncAsync(client, 0);
        var rows = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        // Assert
        var row = Assert.Single(rows!);
        Assert.Equal(1, row.GetProperty("id").GetInt64());
        Assert.Equal("2026-10-02", row.GetProperty("bookingDate").GetString());
        Assert.Equal("Corrected merchant", row.GetProperty("remittanceInformation").GetString());
        Assert.False(row.TryGetProperty("effectiveDate", out _));
        Assert.False(row.TryGetProperty("transactionDate", out _));
        Assert.False(row.TryGetProperty("valueDate", out _));
    }

    [Fact]
    public async Task Sync_ShouldRetainIdenticalPurchases_WhenReferencesDiffer()
    {
        // Arrange
        using var factory = Factory();
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("purchase-a"), Movement("purchase-b")));
        using var client = factory.CreateClient();

        // Act
        await SyncAsync(client, 2);
        var rows = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        // Assert
        Assert.Equal(2, rows!.Length);
        Assert.Equal(2, rows.Select(row => row.GetProperty("id").GetInt64()).Distinct().Count());
        Assert.All(rows, row => Assert.Equal("12.34", row.GetProperty("amount").GetString()));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("duplicate", true)]
    [InlineData("reference", false)]
    public async Task Sync_ShouldPreserveMultiplicityAcrossPages_WhenIdentityIsMissingOrDuplicated(string? reference, bool hasAccountHash)
    {
        // Arrange
        using var factory = Factory(hasAccountHash: hasAccountHash);
        var first = PageWithContinuation("next", Movement(reference));
        var second = Page(Movement(reference));
        factory.EnableBankingTransport.Enqueue(200, first);
        factory.EnableBankingTransport.Enqueue(200, second);
        factory.EnableBankingTransport.Enqueue(200, first);
        factory.EnableBankingTransport.Enqueue(200, second);
        using var client = factory.CreateClient();
        await SyncAsync(client, 2);

        // Act
        await SyncAsync(client, 0);
        var rows = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        // Assert
        Assert.Equal(2, rows!.Length);
    }

    [Fact]
    public async Task Sync_ShouldRetainConflictAndStopRefreshingReference_WhenReferenceIsReused()
    {
        // Arrange
        using var factory = Factory();
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reused")));
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reused", amount: "19.99")));
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reused", amount: "19.9900")));
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reused", date: "2026-10-02")));
        using var client = factory.CreateClient();
        await SyncAsync(client, 1);

        // Act
        await SyncAsync(client, 1);
        await SyncAsync(client, 0);
        await SyncAsync(client, 1);
        var rows = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        // Assert
        Assert.Equal(3, rows!.Length);
        var original = rows.Single(row => row.GetProperty("id").GetInt64() == 1);
        Assert.Equal("2026-10-01", original.GetProperty("bookingDate").GetString());
        Assert.Equal("12.34", original.GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Sync_ShouldRefreshIgnoredFieldsWithoutResettingDecision_WhenReferenceIsReliable()
    {
        // Arrange
        using var factory = Factory();
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference")));
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference", date: "2026-10-02")));
        using var client = factory.CreateClient();
        await SyncAsync(client, 1);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/BankTransaction/1/ignore", null)).StatusCode);

        // Act
        await SyncAsync(client, 0);
        var row = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);

        // Assert
        Assert.Equal("ignored", row.GetProperty("reviewStatus").GetString());
        Assert.Equal("2026-10-02", row.GetProperty("bookingDate").GetString());
    }

    [Fact]
    public async Task Sync_ShouldLeaveMatchedMovementAndExpenseUnchanged_WhenProviderCorrectsFields()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.SeedExpense(7, "2026-10-01T12:00:00Z", "12.34", "Restaurants", description: "Lunch");
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference")));
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference", date: "2026-10-02", description: "Changed")));
        using var client = factory.CreateClient();
        await SyncAsync(client, 1);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/BankTransaction/1/match", new { expenseId = 7 })).StatusCode);
        var before = await client.GetStringAsync("/BankTransaction");
        var expenseBefore = await client.GetStringAsync("/Expense/7");

        // Act
        await SyncAsync(client, 0);

        // Assert
        Assert.Equal(before, await client.GetStringAsync("/BankTransaction"));
        Assert.Equal(expenseBefore, await client.GetStringAsync("/Expense/7"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/BankTransaction/1/unmatch", new { expenseId = 7 })).StatusCode);
    }

    [Fact]
    public async Task Sync_ShouldNotWriteDelayedSuggestion_WhenUserMatchesDuringProviderDecision()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.SeedExpense(7, "2026-10-01T12:00:00Z", "12.34", "Restaurants", description: "Lunch");
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference")));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.OpenRouterHandler.BeforeDecisionAsync = async cancellationToken =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        using var client = factory.CreateClient();
        var sync = client.PostAsync("/BankTransaction/sync", null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/BankTransaction/1/match", new { expenseId = 7 })).StatusCode);
        var before = await client.GetStringAsync("/BankTransaction");

        // Act
        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await sync).StatusCode);

        // Assert
        Assert.Equal(before, await client.GetStringAsync("/BankTransaction"));
        var row = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("suggestedCategory").ValueKind);
    }

    [Fact]
    public async Task CallbackAndSync_ShouldKeepCanonicalIdentity_WhenConsentUidChanges()
    {
        // Arrange
        using var factory = Factory(emptyAccounts: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        factory.EnableBankingTransport.Enqueue(200, Session("first-uid", "primary-hash"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/BankAuth/callback?code=first")).StatusCode);
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference")));
        await SyncAsync(client, 1);
        var before = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);

        // Act
        factory.EnableBankingTransport.Enqueue(200, Session("renewed-uid", "primary-hash"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/BankAuth/callback?code=renewed")).StatusCode);
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference", date: "2026-10-02")));
        await SyncAsync(client, 0);
        var after = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);

        // Assert
        Assert.Equal(before.GetProperty("accountUid").GetString(), after.GetProperty("accountUid").GetString());
        Assert.Equal(before.GetProperty("id").GetInt64(), after.GetProperty("id").GetInt64());
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/BankConnection")).GetProperty("accounts").EnumerateArray());
        Assert.StartsWith("accounts/renewed-uid/transactions?", factory.EnableBankingTransport.Requests.Last().Path);
    }

    [Fact]
    public async Task CallbackAndSync_ShouldKeepSessionLocator_WhenHashlessConsentReusesCanonicalUid()
    {
        // Arrange
        using var factory = Factory(emptyAccounts: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        factory.EnableBankingTransport.Enqueue(200, Session("original-fetch", "primary-hash"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/BankAuth/callback?code=first")).StatusCode);
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference")));
        await SyncAsync(client, 1);
        var before = await client.GetStringAsync("/BankConnection");
        var canonicalUid = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!)
            .GetProperty("accountUid").GetString()!;

        // Act
        factory.EnableBankingTransport.Enqueue(200, Session(canonicalUid, null));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/BankAuth/callback?code=hashless")).StatusCode);
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference", date: "2026-10-02")));
        await SyncAsync(client, 0);

        // Assert
        Assert.Equal(before, await client.GetStringAsync("/BankConnection"));
        Assert.StartsWith("accounts/original-fetch/transactions?", factory.EnableBankingTransport.Requests.Last().Path);
        var row = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal(canonicalUid, row.GetProperty("accountUid").GetString());
        Assert.Equal("2026-10-02", row.GetProperty("bookingDate").GetString());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("hash-a", "hash-b")]
    public async Task Callback_ShouldNotMergeByIbanNameOrSecondaryHash_WhenPrimaryIdentityDiffers(string? firstHash, string? secondHash)
    {
        // Arrange
        using var factory = Factory(emptyAccounts: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        factory.EnableBankingTransport.Enqueue(200, Session("uid-a", firstHash));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/BankAuth/callback?code=first")).StatusCode);
        factory.EnableBankingTransport.Enqueue(200, Session("uid-b", secondHash));

        // Act
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/BankAuth/callback?code=second")).StatusCode);
        var accounts = (await client.GetFromJsonAsync<JsonElement>("/BankConnection")).GetProperty("accounts");

        // Assert
        Assert.Equal(2, accounts.GetArrayLength());
    }

    [Fact]
    public async Task Sync_ShouldNotRefreshMovement_WhenMatchWinsBeforeFinalImportWrite()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.SeedExpense(7, "2026-10-01T12:00:00Z", "12.34", "Restaurants", description: "Lunch");
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference")));
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference", date: "2026-10-02", description: "Changed")));
        using var client = factory.CreateClient();
        await SyncAsync(client, 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Postgres.BeforeBankImportAsync = async (sql, cancellationToken) =>
        {
            if (!sql.StartsWith("WITH identity_choice", StringComparison.Ordinal)) return;
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        var sync = client.PostAsync("/BankTransaction/sync", null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/BankTransaction/1/match", new { expenseId = 7 })).StatusCode);
        var before = await client.GetStringAsync("/BankTransaction");

        // Act
        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await sync).StatusCode);

        // Assert
        Assert.Equal(before, await client.GetStringAsync("/BankTransaction"));
    }

    [Fact]
    public async Task Sync_ShouldScopeReferenceEvidenceToCanonicalAccount_WhenBanksReuseReferences()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.SeedBankAccount("other-canonical", "other-hash");
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("shared", amount: "12.34")));
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("shared", amount: "19.99")));
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("shared", date: "2026-10-02", amount: "12.34")));
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("shared", date: "2026-10-02", amount: "19.99")));
        using var client = factory.CreateClient();
        await SyncAsync(client, 2);

        // Act
        await SyncAsync(client, 0);
        var rows = (await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!;

        // Assert
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal("2026-10-02", row.GetProperty("bookingDate").GetString()));
        Assert.Equal(2, rows.Select(row => row.GetProperty("accountUid").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task Sync_ShouldBindDistinctNullableSourceDates_WithoutChangingBookingDateWireContract()
    {
        // Arrange
        using var factory = Factory();
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("dated")));
        factory.EnableBankingTransport.Enqueue(200, """
            {"transactions":[{"entry_reference":"undated", "booking_date":"2026-10-02",
              "transaction_amount":{"amount":"12.34","currency":"EUR"},
              "credit_debit_indicator":"DBIT","status":"BOOK"}]}
            """);
        using var client = factory.CreateClient();

        // Act
        await SyncAsync(client, 1);
        await SyncAsync(client, 1);
        var rows = (await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!;

        // Assert — source fields cannot yet be read on the #159 wire, so verify the outgoing transport bindings.
        var imports = ImportBindings(factory);
        AssertImportBindings(imports[0]);
        Assert.Equal("2026-09-30", imports[0]["transaction_date"].StringValue);
        Assert.Equal("2026-10-03", imports[0]["value_date"].StringValue);
        Assert.True(imports[1]["transaction_date"].IsNull);
        Assert.True(imports[1]["value_date"].IsNull);
        Assert.Equal("2026-10-01", rows.Single(row => row.GetProperty("id").GetInt64() == 1).GetProperty("bookingDate").GetString());
    }

    [Fact]
    public async Task Sync_ShouldStoreNullRemittance_WhenProviderOmitsDescription()
    {
        // Arrange
        using var factory = Factory();
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference", description: null)));
        using var client = factory.CreateClient();

        // Act
        await SyncAsync(client, 1);
        var row = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);

        // Assert
        Assert.Equal(JsonValueKind.Null, row.GetProperty("remittanceInformation").ValueKind);
        Assert.True(Assert.Single(ImportBindings(factory))["remittance_information"].IsNull);
    }

    [Fact]
    public async Task Sync_ShouldCancelImportTransport_WhenHttpRequestIsCancelled()
    {
        // Arrange
        using var factory = Factory();
        factory.EnableBankingTransport.Enqueue(200, Page(Movement("reference")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockImportUntilCancelled(factory, entered, cancelled);
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var request = SendSyncWithCancellationAsync(factory, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // Act
        cancellation.Cancel();

        // Assert
        Assert.Equal(499, (await request.WaitAsync(TimeSpan.FromSeconds(60))).Response.StatusCode);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
    }

    [Fact]
    public async Task Sync_ShouldKeepLegacyIdsAndLinks_WhenNewFallbackHasIdenticalContent()
    {
        // Arrange
        using var factory = Factory(hasAccountHash: false);
        factory.Postgres.SeedExpense(7, "2026-10-01T12:00:00Z", "12.34", "Restaurants", description: "Lunch");
        factory.Postgres.LinkedTransactions[40] = (7, "matched");
        factory.Postgres.BankDetails[40] = ("2026-10-01", "12.34", "EUR", "Lunch");
        factory.EnableBankingTransport.Enqueue(200, Page(Movement(null), Movement(null)));
        factory.EnableBankingTransport.Enqueue(200, Page(Movement(null), Movement(null)));
        using var client = factory.CreateClient();
        await SyncAsync(client, 2);

        // Act
        await SyncAsync(client, 0);
        var rows = (await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!;

        // Assert
        Assert.Equal(3, rows.Length);
        var legacy = rows.Single(row => row.GetProperty("id").GetInt64() == 40);
        Assert.Equal("matched", legacy.GetProperty("reviewStatus").GetString());
        Assert.Equal(7, legacy.GetProperty("linkedExpense").GetProperty("id").GetInt32());
    }

    private static void BlockImportUntilCancelled(CustomWebApplicationFactory factory, TaskCompletionSource entered, TaskCompletionSource cancelled) =>
        factory.Postgres.BeforeBankImportAsync = async (sql, cancellationToken) =>
        {
            if (!sql.StartsWith("WITH identity_choice", StringComparison.Ordinal)) return;
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
        };

    private static Task<Microsoft.AspNetCore.Http.HttpContext> SendSyncWithCancellationAsync(CustomWebApplicationFactory factory, CancellationToken cancellationToken) =>
        factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/BankTransaction/sync";
            context.RequestAborted = cancellationToken;
        });

    private static Dictionary<string, PostgresValue>[] ImportBindings(CustomWebApplicationFactory factory) =>
        factory.Postgres.ExecutedSql.Select((sql, index) => (sql, index))
            .Where(call => call.sql.StartsWith("WITH identity_choice", StringComparison.Ordinal))
            .Select(call => factory.Postgres.ExecutedParameters[call.index]!.ToDictionary(parameter => parameter.Name, parameter => parameter.Value)).ToArray();

    private static void AssertImportBindings(Dictionary<string, PostgresValue> values)
    {
        Assert.Equal(13, values.Count);
        Assert.Equal("canonical", values["account_uid"].StringValue);
        Assert.Equal("2026-10-01", values["booking_date"].StringValue);
        Assert.Equal("12.34", values["amount"].StringValue);
        Assert.Equal("EUR", values["currency"].StringValue);
        Assert.Equal("DBIT", values["direction"].StringValue);
        Assert.Equal("Lunch", values["remittance_information"].StringValue);
        Assert.Equal("BOOK", values["status"].StringValue);
        Assert.Equal("dated", values["entry_reference"].StringValue);
        Assert.True(values["identity_reliable"].BooleanValue);
        Assert.False(string.IsNullOrEmpty(values["sync_key"].StringValue));
        Assert.StartsWith("ambiguous:", values["fallback_sync_key"].StringValue);
    }

    private static CustomWebApplicationFactory Factory(bool emptyAccounts = false, bool hasAccountHash = true)
    {
        var factory = new CustomWebApplicationFactory
        {
            UseEnableBankingProvider = true, UseRealBankTransactionRepository = true,
            UseRealBankAccountRepository = true, UseRealCategoriesRepository = true, UiOrigin = "https://guito.test"
        };
        if (!emptyAccounts) factory.Postgres.SeedBankAccount("canonical", hasAccountHash ? "hash" : null);
        factory.Postgres.HasBankReviewSchema = true;
        factory.Postgres.PersistBankImports = true;
        factory.Postgres.NextTransactionId = "bank-identity";
        return factory;
    }

    private static async Task SyncAsync(HttpClient client, int expectedNew)
    {
        using var response = await client.PostAsync("/BankTransaction/sync", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedNew, result.GetProperty("new").GetInt32());
    }

    private static object Movement(string? reference, string date = "2026-10-01", string amount = "12.34", string? description = "Lunch") => new
    {
        entry_reference = reference, booking_date = date, transaction_date = "2026-09-30", value_date = "2026-10-03",
        transaction_amount = new { amount, currency = "EUR" },
        credit_debit_indicator = "DBIT", status = "BOOK", remittance_information = description is null ? null : new[] { description }
    };

    private static string Page(params object[] movements) => PageWithContinuation(null, movements);
    private static string PageWithContinuation(string? continuation, params object[] movements) =>
        JsonSerializer.Serialize(new { transactions = movements, continuation_key = continuation });

    private static string Session(string uid, string? hash) => JsonSerializer.Serialize(new
    {
        session_id = $"session-{uid}", aspsp = new { name = "Bank", country = "PT" },
        access = new { valid_until = "2027-01-01T00:00:00Z" },
        accounts = new[] { new { uid, identification_hash = hash, identification_hashes = new[] { "shared-secondary" },
            name = "Main", currency = "EUR", account_id = new { iban = "PT501234567890" } } }
    });
}
