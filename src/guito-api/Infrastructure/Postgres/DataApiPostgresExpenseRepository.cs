using System.Globalization;
using GuitoApi.Infrastructure.Serialization;
using GuitoApi.Model;
using GuitoApi.Services;
using GuitoApi.Repositories;
using GuitoApi.Exceptions;

namespace GuitoApi.Infrastructure.Postgres;

public class DataApiPostgresExpenseRepository(IPostgresDataApiClient client, IUnitOfWork unitOfWork) : IExpenseRepository
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
    public Task DeleteSmokeAsync(int id, CancellationToken cancellationToken = default) =>
        throw new ProblemException(409, "Postgres expenses cannot be deleted through Smoke cleanup.");

    private static PostgresParameter Text(string name, string value) => new(name, PostgresValue.FromString(value));

    private const string ReadSql = """
        SELECT CAST(e.id AS text), CAST(e.booking_date AS text), CAST(e.amount AS text),
               e.description, c.name, e.creator_email, CAST(e.create_date AS text),
               CAST(e.update_date AS text), CAST(e.revision AS text)
        FROM expenses e JOIN categories c ON c.id = e.category_id
        WHERE e.id = :id
        """;

    private const string UpdateSql = """
        UPDATE expenses
        SET description = :description, category_id = :category_id,
            amount = CASE WHEN amount = CAST(:amount AS numeric) THEN amount ELSE CAST(:amount AS numeric) END,
            booking_date = CAST(:booking_date AS timestamptz),
            update_date = clock_timestamp(), revision = revision + 1
        WHERE id = :id AND revision = :revision
        """;

    public async Task<ExpenseDetail> UpdateAsync(int id, long revision, ExpenseEdit value, CancellationToken cancellationToken = default)
    {
        await using var transaction = unitOfWork;
        await transaction.BeginAsync(cancellationToken);
        await LockAsync(id, cancellationToken);
        var categoryId = await ResolveCategoryIdAsync(value.Category, cancellationToken);
        var result = await client.ExecuteAsync(UpdateSql, UpdateParameters(id, revision, value, categoryId),
            cancellationToken: cancellationToken);
        if (result.NumberOfRecordsUpdated != 1) throw new ProblemException(412, "Expense revision is stale.");
        var detail = await ReadAsync(id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return detail;
    }

    public async Task DeleteAsync(int id, long revision, CancellationToken cancellationToken = default)
    {
        await using var transaction = unitOfWork;
        await transaction.BeginAsync(cancellationToken);
        await LockAsync(id, cancellationToken);
        var detail = await ReadAsync(id, cancellationToken);
        if (detail.Revision != revision.ToString(CultureInfo.InvariantCulture)) throw new ProblemException(412, "Expense revision is stale.");
        await UnlinkTransactionsAsync(id, cancellationToken);
        var result = await client.ExecuteAsync("DELETE FROM expenses WHERE id = :id AND revision = :revision",
            [new("id", PostgresValue.FromLong(id)), new("revision", PostgresValue.FromLong(revision))], cancellationToken: cancellationToken);
        if (result.NumberOfRecordsUpdated != 1) throw new ProblemException(412, "Expense revision is stale.");
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task UnlinkTransactionsAsync(int id, CancellationToken cancellationToken)
    {
        // #133's independently deployed migration changes both link type and its state constraint.
        var schema = await client.ExecuteAsync("""
            SELECT column_name, data_type FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = 'bank_transactions'
              AND column_name IN ('expense_id', 'review_status')
            """, cancellationToken: cancellationToken);
        var columns = schema.Rows.ToDictionary(row => row[0].StringValue!, row => row[1].StringValue!);
        var reviewSchema = columns.GetValueOrDefault("expense_id") == "integer"
            && columns.GetValueOrDefault("review_status") == "text";
        if (!reviewSchema && (columns.GetValueOrDefault("expense_id") != "text" || columns.ContainsKey("review_status")))
            throw new ProblemException(409, "Bank link schema is unsupported; deletion was not committed.");
        var sql = reviewSchema
            ? "UPDATE bank_transactions SET expense_id = NULL, review_status = 'needsReview' WHERE expense_id = :expense_id"
            : "UPDATE bank_transactions SET expense_id = NULL WHERE expense_id = :expense_id";
        var value = reviewSchema ? PostgresValue.FromLong(id) : PostgresValue.FromString(id.ToString(CultureInfo.InvariantCulture));
        await client.ExecuteAsync(sql, [new("expense_id", value)], cancellationToken: cancellationToken);
    }

    private async Task LockAsync(int id, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteAsync("SELECT id FROM expenses WHERE id = :id FOR UPDATE",
            [new("id", PostgresValue.FromLong(id))], cancellationToken: cancellationToken);
        if (result.Rows.Count == 0) throw new ProblemException(404, "Expense not found.");
    }

    private static IReadOnlyList<PostgresParameter> UpdateParameters(int id, long revision, ExpenseEdit value, long categoryId) =>
    [
        new("id", PostgresValue.FromLong(id)), new("revision", PostgresValue.FromLong(revision)),
        new("category_id", PostgresValue.FromLong(categoryId)), Text("description", value.Description),
        Text("amount", value.Amount.ToString(CultureInfo.InvariantCulture)),
        Text("booking_date", value.BookingDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
    ];

    public async Task<ExpenseDetail> ReadAsync(int id, CancellationToken cancellationToken = default)
    {
        var result = await client.ExecuteAsync(ReadSql, [new("id", PostgresValue.FromLong(id))],
            cancellationToken: cancellationToken);
        if (result.Rows.Count == 0) throw new ProblemException(404, "Expense not found.");
        return ToDetail(result.Rows.Single());
    }

    private static ExpenseDetail ToDetail(IReadOnlyList<PostgresValue> row)
    {
        try { _ = ExactDecimalJsonConverter.ParseExact(row[2].StringValue!); }
        catch (System.Text.Json.JsonException) { throw new ProblemException(502, "Stored amount is outside the exact decimal contract."); }
        return new(int.Parse(row[0].StringValue!, CultureInfo.InvariantCulture), row[2].StringValue!,
            row[3].StringValue!, row[4].StringValue!, ReadInstant(row[1])!.Value, row[5].StringValue!,
            ReadInstant(row[6])!.Value, ReadInstant(row[7]), row[8].StringValue!);
    }
}
