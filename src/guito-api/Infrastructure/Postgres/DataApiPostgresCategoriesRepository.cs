using System.Globalization;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Infrastructure.Postgres;

/// <summary>
/// Authoritative category reader over the existing Data API table.
/// Category maintenance is external to the app; requests never seed or create rows.
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
