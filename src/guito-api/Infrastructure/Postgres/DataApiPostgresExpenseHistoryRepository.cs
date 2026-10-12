using System.Globalization;
using GuitoApi.Model;
using GuitoApi.Repositories;
using GuitoApi.Services;

namespace GuitoApi.Infrastructure.Postgres;

public sealed class DataApiPostgresExpenseHistoryRepository(IPostgresDataApiClient client) : IExpenseHistoryRepository
{
    // One statement shares an MVCC view for count and payload, including empty/out-of-range pages.
    private const string HistorySql = """
        SELECT CAST(t.total_count AS text), CAST(p.id AS text), CAST(p.booking_date AS text),
               CAST(p.amount AS text), p.description, p.name, p.creator_email,
               CAST(p.create_date AS text), CAST(p.update_date AS text)
        FROM (SELECT COUNT(*) AS total_count FROM expenses) t
        LEFT JOIN LATERAL (
            SELECT e.id, e.booking_date, e.amount, e.description, c.name,
                   e.creator_email, e.create_date, e.update_date
            FROM expenses e JOIN categories c ON c.id = e.category_id
            ORDER BY e.booking_date DESC, e.id DESC LIMIT :limit OFFSET :offset
        ) p ON true
        ORDER BY p.booking_date DESC, p.id DESC
        """;

    public async Task<ExpenseHistoryPage> ReadAsync(int pageSize, long offset, CancellationToken cancellationToken = default)
    {
        var result = await client.ExecuteAsync(HistorySql,
            [new("limit", PostgresValue.FromLong(pageSize)), new("offset", PostgresValue.FromLong(offset))],
            cancellationToken: cancellationToken);
        var total = long.Parse(result.Rows[0][0].StringValue!, CultureInfo.InvariantCulture);
        return new(total, result.Rows.Where(row => !row[1].IsNull).Select(ToEntry).ToList());
    }

    private static ExpenseHistoryEntry ToEntry(IReadOnlyList<PostgresValue> row) => new(
        int.Parse(row[1].StringValue!, CultureInfo.InvariantCulture), row[3].StringValue!,
        ReadInstant(row[2])!.Value, row[4].StringValue, row[5].StringValue, row[6].StringValue,
        ReadInstant(row[7]), ReadInstant(row[8]));

    private static DateTimeOffset? ReadInstant(PostgresValue value) => value.IsNull ? null
        : TimeService.InLisbon(DateTimeOffset.Parse(value.StringValue!, CultureInfo.InvariantCulture));
}
