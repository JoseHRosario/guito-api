using System.Globalization;
using GuitoApi.Model;
using GuitoApi.Repositories;
using GuitoApi.Exceptions;

namespace GuitoApi.Infrastructure.Postgres;

public class DataApiPostgresExpenseRepository(IPostgresDataApiClient client) : IExpenseRepository
{
    private const string CreateSql = """
        INSERT INTO expenses (amount, description, category_id, creator_email, booking_date)
        VALUES (CAST(:amount AS numeric), :description, :category_id,
                :creator_email, CAST(:booking_date AS timestamptz))
        RETURNING id
        """;
    private const string LatestSql = """
        SELECT CAST(e.id AS text), CAST(e.booking_date AS text), CAST(e.amount AS text),
               e.description, c.name, e.creator_email, CAST(e.create_date AS text), CAST(e.update_date AS text)
        FROM expenses e JOIN categories c ON c.id = e.category_id
        ORDER BY e.booking_date DESC, e.id DESC LIMIT :limit
        """;

    public async Task<string> CreateAsync(ExpenseDraft value, CancellationToken cancellationToken = default)
    {
        var categoryId = await ResolveCategoryIdAsync(value.Category, cancellationToken);
        var result = await client.ExecuteAsync(CreateSql,
            [Text("amount", value.Amount.ToString(CultureInfo.InvariantCulture)),
             Text("description", value.Description), new("category_id", PostgresValue.FromLong(categoryId)),
             Text("creator_email", value.CreatorEmail),
             Text("booking_date", value.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))],
            cancellationToken: cancellationToken);
        return ReadId(result.Rows.Single()[0]).ToString(CultureInfo.InvariantCulture);
    }

    private async Task<long> ResolveCategoryIdAsync(string name, CancellationToken cancellationToken)
    {
        // The mirror is seeded separately; a request must never invent a category.
        var result = await client.ExecuteAsync("SELECT id FROM categories WHERE name = :name",
            [Text("name", name)], cancellationToken: cancellationToken);
        if (result.Rows.Count == 0)
            throw new ProblemException(422, "Category is not present in the Postgres categories mirror.");
        return ReadId(result.Rows.Single()[0]);
    }

    private static long ReadId(PostgresValue value) => value.StringValue is { } text
        ? long.Parse(text, CultureInfo.InvariantCulture) : value.LongValue!.Value;

    public async Task<IReadOnlyList<ExpenseSummary>> ListLatestAsync(int limit, CancellationToken cancellationToken = default)
    {
        var result = await client.ExecuteAsync(LatestSql,
            [new("limit", PostgresValue.FromLong(limit))], cancellationToken: cancellationToken);
        return result.Rows.Select(ToSummary).ToList();
    }

    private static ExpenseSummary ToSummary(IReadOnlyList<PostgresValue> row)
    {
        var occurredAt = ReadInstant(row[1])!.Value;
        return new ExpenseSummary
        {
            Id = row[0].StringValue!, Date = occurredAt.Date, OccurredAt = occurredAt,
            Amount = decimal.Parse(row[2].StringValue!, CultureInfo.InvariantCulture), Currency = "EUR",
            Description = row[3].StringValue, Category = row[4].StringValue, CreatorEmail = row[5].StringValue,
            CreationDate = ReadInstant(row[6]), UpdateDate = ReadInstant(row[7])
        };
    }

    private static DateTimeOffset? ReadInstant(PostgresValue value) => value.IsNull ? null
        : ExpenseTime.InLisbon(DateTimeOffset.Parse(value.StringValue!, CultureInfo.InvariantCulture));

    // Smoke cleanup is a Sheets-only capability, never a personal-record delete.
    public Task DeleteAsync(int id, CancellationToken cancellationToken = default) =>
        throw new ProblemException(409, "Postgres expenses cannot be deleted through Smoke cleanup.");

    private static PostgresParameter Text(string name, string value) => new(name, PostgresValue.FromString(value));
}
