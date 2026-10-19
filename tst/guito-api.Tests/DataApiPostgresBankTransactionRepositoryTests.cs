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
    public async Task ImportAsync_ShouldReturnTrue_AndExecuteInsert_WhenRowIsNew()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(
            new PostgresResult(["id", "sync_key", "inserted"], [[PostgresValue.FromLong(1), PostgresValue.FromString("sync-key-1"), PostgresValue.FromBoolean(true)]], 1));
        var repository = new DataApiPostgresBankTransactionRepository(client, new DataApiUnitOfWork(client, new PostgresTransactionContext()));

        var isNew = await repository.ImportAsync(BuildInsert());

        Assert.True(isNew.IsNew);
        var sql = Assert.Single(client.ExecutedSql);
        Assert.Contains("INSERT INTO bank_transactions", sql);
        Assert.Contains("ON CONFLICT (sync_key) DO UPDATE", sql);
        Assert.Contains("RETURNING id", sql);
    }

    [Fact]
    public async Task ImportAsync_ShouldReturnFalse_WhenSyncKeyConflicts()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult([], [], 0));
        var repository = new DataApiPostgresBankTransactionRepository(client, new DataApiUnitOfWork(client, new PostgresTransactionContext()));

        var isNew = await repository.ImportAsync(BuildInsert());

        Assert.False(isNew.IsNew);
    }

    [Fact]
    public async Task ImportAsync_ShouldBindEveryColumn_AsNamedParameter()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult([], [], 0));
        var repository = new DataApiPostgresBankTransactionRepository(client, new DataApiUnitOfWork(client, new PostgresTransactionContext()));

        await repository.ImportAsync(BuildInsert());

        var parameters = Assert.Single(client.ExecutedParameters)!;
        var byName = parameters.ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(13, parameters.Count);
        Assert.Equal("eb-account-1", byName["account_uid"].StringValue);
        Assert.Equal("2026-10-04", byName["booking_date"].StringValue);
        Assert.Equal("2.3", byName["amount"].StringValue);
        Assert.Equal("EUR", byName["currency"].StringValue);
        Assert.Equal("DBIT", byName["direction"].StringValue);
        Assert.Equal("Padaria", byName["remittance_information"].StringValue);
        Assert.Equal("BOOK", byName["status"].StringValue);
        Assert.Equal("sync-key-1", byName["sync_key"].StringValue);
    }

    [Fact]
    public async Task ImportAsync_ShouldBindNullRemittance_WhenItIsAbsent()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult([], [], 0));
        var repository = new DataApiPostgresBankTransactionRepository(client, new DataApiUnitOfWork(client, new PostgresTransactionContext()));

        await repository.ImportAsync(BuildInsert(remittanceInformation: null));

        var parameters = Assert.Single(client.ExecutedParameters)!;
        Assert.True(parameters.Single(p => p.Name == "remittance_information").Value.IsNull);
    }

    [Fact]
    public async Task ImportAsync_ShouldForwardCancellationToken()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult([], [], 0));
        var repository = new DataApiPostgresBankTransactionRepository(client, new DataApiUnitOfWork(client, new PostgresTransactionContext()));
        using var cancellationSource = new CancellationTokenSource();

        await repository.ImportAsync(BuildInsert(), cancellationSource.Token);

        Assert.Equal(cancellationSource.Token, Assert.Single(client.ExecutedCancellationTokens));
    }

    [Fact]
    public async Task UpdateSuggestedCategoryAsync_ShouldBindSyncKeyAndCategory_AsNamedParameters()
    {
        var client = new FakePostgresDataApiClient();
        var repository = new DataApiPostgresBankTransactionRepository(client, new DataApiUnitOfWork(client, new PostgresTransactionContext()));

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
        var repository = new DataApiPostgresBankTransactionRepository(client, new DataApiUnitOfWork(client, new PostgresTransactionContext()));

        await repository.UpdateSuggestedCategoryAsync("sync-key-1", null);

        var parameters = Assert.Single(client.ExecutedParameters)!;
        Assert.True(parameters.Single(p => p.Name == "category_id").Value.IsNull);
    }
}
