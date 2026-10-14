using System.Globalization;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.Serialization;
using GuitoApi.Model;
using GuitoApi.Repositories;
using GuitoApi.Services;

namespace GuitoApi.Infrastructure.Postgres;

public sealed class DataApiPostgresExpenseDetailRepository(IPostgresDataApiClient client, IUnitOfWork unitOfWork) : IExpenseDetailRepository
{
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
        var categoryId = await ResolveCategoryAsync(value.Category, cancellationToken);
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

    private async Task<long> ResolveCategoryAsync(string category, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteAsync("SELECT id FROM categories WHERE name = :name",
            [Text("name", category)], cancellationToken: cancellationToken);
        if (result.Rows.Count == 0) throw new ProblemException(422, "Category is not present in the Postgres categories mirror.");
        var value = result.Rows.Single()[0];
        return value.LongValue ?? long.Parse(value.StringValue!, CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<PostgresParameter> UpdateParameters(int id, long revision, ExpenseEdit value, long categoryId) =>
    [
        new("id", PostgresValue.FromLong(id)), new("revision", PostgresValue.FromLong(revision)),
        new("category_id", PostgresValue.FromLong(categoryId)), Text("description", value.Description),
        Text("amount", value.Amount.ToString(CultureInfo.InvariantCulture)),
        Text("booking_date", value.BookingDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
    ];

    private static PostgresParameter Text(string name, string value) => new(name, PostgresValue.FromString(value));

    public async Task<ExpenseDetail> ReadAsync(int id, CancellationToken cancellationToken = default)
    {
        var result = await client.ExecuteAsync(ReadSql, [new("id", PostgresValue.FromLong(id))],
            cancellationToken: cancellationToken);
        if (result.Rows.Count == 0) throw new ProblemException(404, "Expense not found.");
        return Map(result.Rows.Single());
    }

    private static ExpenseDetail Map(IReadOnlyList<PostgresValue> row)
    {
        try { _ = ExactDecimalJsonConverter.ParseExact(row[2].StringValue!); }
        catch (System.Text.Json.JsonException) { throw new ProblemException(502, "Stored amount is outside the exact decimal contract."); }
        return new(int.Parse(row[0].StringValue!, CultureInfo.InvariantCulture), row[2].StringValue!,
            row[3].StringValue!, row[4].StringValue!, Instant(row[1]), row[5].StringValue!,
            Instant(row[6]), row[7].IsNull ? null : Instant(row[7]), row[8].StringValue!);
    }

    private static DateTimeOffset Instant(PostgresValue value) =>
        TimeService.InLisbon(DateTimeOffset.Parse(value.StringValue!, CultureInfo.InvariantCulture));
}
