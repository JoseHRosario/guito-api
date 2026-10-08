using System.Globalization;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Infrastructure.Postgres;

/// <summary>
/// ICategoriesRepository over the Data API (issue #112, ADR-0014): raw SQL against
/// the categories table, executed through IPostgresDataApiClient. The table is
/// seeded by the numbered migration script (004_seed_categories.sql), not by the app.
/// </summary>
public class DataApiPostgresCategoriesRepository(IPostgresDataApiClient client) : ICategoriesRepository
{
    private const string ListSql = "SELECT id, name, description FROM categories ORDER BY name";

    public async Task<IReadOnlyList<CategorySummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await client.ExecuteAsync(ListSql, cancellationToken: cancellationToken);
        return result.Rows.Select(ToRow).ToList();
    }

    private static CategorySummary ToRow(IReadOnlyList<PostgresValue> row) => new(
        Id: row[0].StringValue is { } id ? long.Parse(id, CultureInfo.InvariantCulture) : row[0].LongValue!.Value,
        Name: row[1].StringValue!,
        Description: row[2].IsNull ? null : row[2].StringValue);
}
