using System.Globalization;
using GuitoApi.Model;
using GuitoApi.Repositories;
using GuitoApi.Exceptions;

namespace GuitoApi.Infrastructure.Postgres;

public class DataApiPostgresExpenseRepository(IPostgresDataApiClient client, IHttpContextAccessor context) : IExpenseRepository
{
    private const string CreateSql = """
        INSERT INTO expenses (id, amount, currency, description, category, creator_email, occurred_at, scope)
        VALUES (:id, CAST(:amount AS numeric), :currency, :description, :category,
                :creator_email, CAST(:occurred_at AS timestamptz), :scope)
        """;
    private const string LatestSql = """
        SELECT id, CAST(occurred_at AS text), CAST(amount AS text), currency,
               description, category, creator_email, CAST(creation_date AS text), CAST(update_date AS text)
        FROM expenses WHERE scope = :scope
        ORDER BY occurred_at DESC, id DESC LIMIT :limit
        """;

    public async Task<string> CreateAsync(ExpenseDraft value, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("D");
        await client.ExecuteAsync(CreateSql,
            [Text("id", id), Text("amount", value.Amount.ToString(CultureInfo.InvariantCulture)),
             Text("currency", value.Currency), Text("description", value.Description), Text("category", value.Category),
             Text("creator_email", value.CreatorEmail), Text("occurred_at", value.OccurredAt.ToUniversalTime().ToString("O")), Text("scope", Scope)],
            cancellationToken: cancellationToken);
        return id;
    }

    public async Task<IReadOnlyList<ExpenseSummary>> ListLatestAsync(int limit, CancellationToken cancellationToken = default)
    {
        var result = await client.ExecuteAsync(
            LatestSql,
            [Text("scope", Scope), new("limit", PostgresValue.FromLong(limit))], cancellationToken: cancellationToken);
        return result.Rows.Select(ToSummary).ToList();
    }

    private static ExpenseSummary ToSummary(IReadOnlyList<PostgresValue> row)
    {
        var occurredAt = ExpenseTime.InLisbon(DateTimeOffset.Parse(row[1].StringValue!, CultureInfo.InvariantCulture));
        return new ExpenseSummary
        {
            Id = row[0].StringValue!, Date = occurredAt.Date, OccurredAt = occurredAt,
            Amount = decimal.Parse(row[2].StringValue!, CultureInfo.InvariantCulture), Currency = row[3].StringValue!,
            Description = row[4].StringValue, Category = row[5].StringValue, CreatorEmail = row[6].StringValue,
            CreationDate = ExpenseTime.InLisbon(DateTimeOffset.Parse(row[7].StringValue!, CultureInfo.InvariantCulture)),
            UpdateDate = ExpenseTime.InLisbon(DateTimeOffset.Parse(row[8].StringValue!, CultureInfo.InvariantCulture))
        };
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var result = await client.ExecuteAsync("DELETE FROM expenses WHERE id = :id AND scope = 'smoke'",
            [Text("id", id)], cancellationToken: cancellationToken);
        if (result.NumberOfRecordsUpdated == 0)
            throw new ExpenseNotFoundException(id);
    }

    private string Scope => string.Equals(context.HttpContext?.Request.Headers["X-Sheet-Scope"].ToString(),
        "smoke", StringComparison.OrdinalIgnoreCase) ? "smoke" : "normal";

    private static PostgresParameter Text(string name, string value) => new(name, PostgresValue.FromString(value));
}
