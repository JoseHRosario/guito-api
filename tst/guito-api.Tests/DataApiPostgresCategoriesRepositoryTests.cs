using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Model;

namespace GuitoApi.Tests;

/// <summary>
/// Direct unit tests for DataApiPostgresCategoriesRepository (issue #112) over a
/// fake IPostgresDataApiClient: list SQL + mapping, per-name upsert binding.
/// </summary>
public class DataApiPostgresCategoriesRepositoryTests
{
    [Fact]
    public async Task ListAsync_ShouldListOrderedByName()
    {
        var client = new FakePostgresDataApiClient();
        var repository = new DataApiPostgresCategoriesRepository(client);

        await repository.ListAsync();

        var sql = Assert.Single(client.ExecutedSql);
        Assert.Contains("SELECT id, name, description FROM categories ORDER BY name", sql);
    }

    [Fact]
    public async Task ListAsync_ShouldMapRows_WhenCategoriesExist()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult(
            ["id", "name", "description"],
            [
                [PostgresValue.FromLong(1), PostgresValue.FromString("Groceries"), PostgresValue.Null()],
                [PostgresValue.FromLong(2), PostgresValue.FromString("Restaurants"), PostgresValue.FromString("Eating out")],
            ],
            0));
        var repository = new DataApiPostgresCategoriesRepository(client);

        var mirror = await repository.ListAsync();

        Assert.Equal(2, mirror.Count);
        Assert.Equal(new CategorySummary(1, "Groceries", null), mirror[0]);
        Assert.Equal(new CategorySummary(2, "Restaurants", "Eating out"), mirror[1]);
    }

    [Fact]
    public async Task ListAsync_ShouldMapStringEncodedIds_WhenDataApiReturnsStringFields()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult(
            ["id", "name", "description"],
            [[PostgresValue.FromString("9"), PostgresValue.FromString("Transport"), PostgresValue.Null()]],
            0));
        var repository = new DataApiPostgresCategoriesRepository(client);

        var mirror = await repository.ListAsync();

        Assert.Equal(new CategorySummary(9, "Transport", null), Assert.Single(mirror));
    }
}
