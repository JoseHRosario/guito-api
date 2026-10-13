using GuitoApi.Model;
using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Repositories;

namespace GuitoApi.Tests;

/// <summary>
/// Direct unit tests for DataApiPostgresBankTransactionRepository (issue #88) over a
/// fake IPostgresDataApiClient: insert-or-skip idempotency by sync_key, pending list
/// filtering and mapping.
/// </summary>
public class DataApiPostgresBankTransactionRepositoryTests
{
    private static BankTransactionInsert BuildInsert(string? remittanceInformation = "Padaria") => new()
    {
        AccountUid = "eb-account-1",
        BookingDate = new DateOnly(2026, 10, 4),
        Amount = 2.30m,
        Currency = "EUR",
        Direction = "DBIT",
        RemittanceInformation = remittanceInformation,
        Status = "BOOK",
        SyncKey = "sync-key-1",
    };

    [Fact]
    public async Task InsertOrSkipAsync_ShouldReturnTrue_AndExecuteInsert_WhenRowIsNew()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(
            new PostgresResult(["id"], [[PostgresValue.FromLong(1)]], 1));
        var repository = new DataApiPostgresBankTransactionRepository(client);

        var isNew = await repository.InsertOrSkipAsync(BuildInsert());

        Assert.True(isNew);
        var sql = Assert.Single(client.ExecutedSql);
        Assert.Contains("INSERT INTO bank_transactions", sql);
        Assert.Contains("ON CONFLICT (sync_key) DO NOTHING", sql);
        Assert.Contains("RETURNING id", sql);
    }

    [Fact]
    public async Task InsertOrSkipAsync_ShouldReturnFalse_WhenSyncKeyConflicts()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult([], [], 0));
        var repository = new DataApiPostgresBankTransactionRepository(client);

        var isNew = await repository.InsertOrSkipAsync(BuildInsert());

        Assert.False(isNew);
    }

    [Fact]
    public async Task InsertOrSkipAsync_ShouldBindEveryColumn_AsNamedParameter()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult([], [], 0));
        var repository = new DataApiPostgresBankTransactionRepository(client);

        await repository.InsertOrSkipAsync(BuildInsert());

        var parameters = Assert.Single(client.ExecutedParameters)!;
        var byName = parameters.ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(8, parameters.Count);
        Assert.Equal("eb-account-1", byName["account_uid"].StringValue);
        Assert.Equal("2026-10-04", byName["booking_date"].StringValue);
        Assert.True(byName["amount"].DoubleValue == 2.30);
        Assert.Equal("EUR", byName["currency"].StringValue);
        Assert.Equal("DBIT", byName["direction"].StringValue);
        Assert.Equal("Padaria", byName["remittance_information"].StringValue);
        Assert.Equal("BOOK", byName["status"].StringValue);
        Assert.Equal("sync-key-1", byName["sync_key"].StringValue);
    }

    [Fact]
    public async Task InsertOrSkipAsync_ShouldBindNullRemittance_WhenItIsAbsent()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult([], [], 0));
        var repository = new DataApiPostgresBankTransactionRepository(client);

        await repository.InsertOrSkipAsync(BuildInsert(remittanceInformation: null));

        var parameters = Assert.Single(client.ExecutedParameters)!;
        Assert.True(parameters.Single(p => p.Name == "remittance_information").Value.IsNull);
    }

    [Fact]
    public async Task InsertOrSkipAsync_ShouldForwardCancellationToken()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult([], [], 0));
        var repository = new DataApiPostgresBankTransactionRepository(client);
        using var cancellationSource = new CancellationTokenSource();

        await repository.InsertOrSkipAsync(BuildInsert(), cancellationSource.Token);

        Assert.Equal(cancellationSource.Token, Assert.Single(client.ExecutedCancellationTokens));
    }

    [Fact]
    public async Task UpdateSuggestedCategoryAsync_ShouldBindSyncKeyAndCategory_AsNamedParameters()
    {
        var client = new FakePostgresDataApiClient();
        var repository = new DataApiPostgresBankTransactionRepository(client);

        await repository.UpdateSuggestedCategoryAsync("sync-key-1", 42);

        var sql = Assert.Single(client.ExecutedSql);
        Assert.Contains("UPDATE bank_transactions", sql);
        Assert.Contains("SET suggested_category_id = :category_id", sql);
        Assert.Contains("WHERE sync_key = :sync_key", sql);
        var parameters = Assert.Single(client.ExecutedParameters)!;
        var byName = parameters.ToDictionary(p => p.Name, p => p.Value);
        // Typed write (bigint column): a string field fails live with a type mismatch.
        Assert.Equal(42L, byName["category_id"].LongValue);
        Assert.Equal("sync-key-1", byName["sync_key"].StringValue);
    }

    [Fact]
    public async Task UpdateSuggestedCategoryAsync_ShouldBindNullCategory_WhenNoSuggestion()
    {
        var client = new FakePostgresDataApiClient();
        var repository = new DataApiPostgresBankTransactionRepository(client);

        await repository.UpdateSuggestedCategoryAsync("sync-key-1", null);

        var parameters = Assert.Single(client.ExecutedParameters)!;
        Assert.True(parameters.Single(p => p.Name == "category_id").Value.IsNull);
    }
}
