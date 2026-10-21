using GuitoApi.Infrastructure.Postgres;

namespace GuitoApi.Tests;

/// <summary>
/// Legacy suggestion-binding tests from issue #88. Import behavior is covered at
/// the HTTP boundary by BankIdentityBoundaryTests.
/// </summary>
public class DataApiPostgresBankTransactionRepositoryTests
{
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
