using System.Globalization;
using GuitoApi.Model;
using GuitoApi.Services;
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

    private const string CategoryMonthSql = """
        SELECT CAST(e.id AS text), CAST(e.booking_date AS text), CAST(e.amount AS text),
               e.description, c.name, e.creator_email, CAST(e.create_date AS text), CAST(e.update_date AS text)
        FROM expenses e JOIN categories c ON c.id = e.category_id
        WHERE e.category_id = :category_id
          AND e.booking_date >= CAST(:start AS timestamptz)
          AND e.booking_date < CAST(:end AS timestamptz)
        ORDER BY e.booking_date DESC, e.id DESC
        """;

    public async Task<int> CreateAsync(ExpenseDraft value, CancellationToken cancellationToken = default)
    {
        var categoryId = await ResolveCategoryIdAsync(value.Category, cancellationToken);
        var result = await client.ExecuteAsync(CreateSql,
            [Text("amount", value.Amount.ToString(CultureInfo.InvariantCulture)),
             Text("description", value.Description), new("category_id", PostgresValue.FromLong(categoryId)),
             Text("creator_email", value.CreatorEmail),
             Text("booking_date", value.BookingDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))],
            cancellationToken: cancellationToken);
        return checked((int)ReadId(result.Rows.Single()[0]));
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

    public async Task<IReadOnlyList<ExpenseSummary>> ListAsync(string category, DateTimeOffset start,
        DateTimeOffset? endExclusiveOrStartOfYear10000, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadAllAsync(category, start, endExclusiveOrStartOfYear10000, cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ProblemException(502, "The complete expense result contains an amount outside the exact decimal contract; no partial result was returned.");
        }
        catch (Amazon.RDSDataService.AmazonRDSDataServiceException)
        {
            // Data API rejects responses above its transport/row limits instead of paging.
            // Never turn that (or a provider outage) into an empty/partial success.
            throw new ProblemException(502, "The Data API could not return the complete expense result (provider failure or response limit); no partial result was returned.");
        }
    }

    private async Task<IReadOnlyList<ExpenseSummary>> ReadAllAsync(string category, DateTimeOffset start,
        DateTimeOffset? endExclusiveOrStartOfYear10000, CancellationToken cancellationToken)
    {
        var categoryId = await ResolveCategoryIdAsync(category, cancellationToken);
        var result = await client.ExecuteAsync(CategoryMonthSql,
            [new("category_id", PostgresValue.FromLong(categoryId)),
             Text("start", start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
             // PostgreSQL supports year 10000; DateTimeOffset does not. Null is the
             // exclusive upper boundary of the last supported four-digit month.
             Text("end", endExclusiveOrStartOfYear10000?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "10000-01-01T00:00:00+00:00")],
            cancellationToken: cancellationToken);
        return result.Rows.Select(ToExactSummary).ToList();
    }

    private static ExpenseSummary ToExactSummary(IReadOnlyList<PostgresValue> row)
    {
        // Existing read fields, but never allow decimal.Parse's rounding of an
        // unconstrained PostgreSQL numeric to turn this complete result into false success.
        _ = Infrastructure.Serialization.ExactDecimalJsonConverter.ParseExact(row[2].StringValue!);
        return ToSummary(row);
    }

    private static ExpenseSummary ToSummary(IReadOnlyList<PostgresValue> row)
    {
        var bookingDate = ReadInstant(row[1])!.Value;
        return new ExpenseSummary
        {
            Id = checked((int)ReadId(row[0])), BookingDate = bookingDate,
            Amount = decimal.Parse(row[2].StringValue!, CultureInfo.InvariantCulture), Currency = "EUR",
            Description = row[3].StringValue, Category = row[4].StringValue, CreatorEmail = row[5].StringValue,
            CreationDate = ReadInstant(row[6]), UpdateDate = ReadInstant(row[7])
        };
    }

    private static DateTimeOffset? ReadInstant(PostgresValue value) => value.IsNull ? null
        : TimeService.InLisbon(DateTimeOffset.Parse(value.StringValue!, CultureInfo.InvariantCulture));

    // Smoke cleanup is a Sheets-only capability, never a personal-record delete.
    public Task DeleteAsync(int id, CancellationToken cancellationToken = default) =>
        throw new ProblemException(409, "Postgres expenses cannot be deleted through Smoke cleanup.");

    private static PostgresParameter Text(string name, string value) => new(name, PostgresValue.FromString(value));
}
