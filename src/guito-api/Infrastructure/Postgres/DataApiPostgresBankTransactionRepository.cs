using System.Globalization;
using GuitoApi.Model;
using GuitoApi.Repositories;
using GuitoApi.Services;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.Serialization;

namespace GuitoApi.Infrastructure.Postgres;

/// <summary>
/// IBankTransactionRepository over the Data API (issue #88, ADR-0013): raw SQL +
/// small mappers against the bank_transactions table, executed through
/// IPostgresDataApiClient (a statement inside an open unit of work joins its
/// transaction automatically — the repository never names a transaction id).
/// </summary>
public class DataApiPostgresBankTransactionRepository(IPostgresDataApiClient client, IUnitOfWork unitOfWork) : IBankTransactionRepository
{
    private const string InsertSql = """
        INSERT INTO bank_transactions
            (account_uid, booking_date, amount, currency, direction, remittance_information, status, sync_key)
        VALUES
            (:account_uid, :booking_date::date, :amount, :currency, :direction, :remittance_information, :status, :sync_key)
        ON CONFLICT (sync_key) DO NOTHING
        RETURNING id
        """;

    private const string ProjectionSql = $"""
        SELECT bt.id, bt.account_uid, bt.booking_date, CAST(bt.amount AS text), bt.currency,
               bt.remittance_information, c.id, c.name, bt.status, bt.review_status,
               e.id, CAST(e.booking_date AS text), CAST(e.amount AS text), e.description, ec.name,
               (SELECT COUNT(*) FROM expenses candidate WHERE {EligibilitySql})
        """;
    private const string JoinsSql = """
        LEFT JOIN categories c ON c.id = bt.suggested_category_id
        LEFT JOIN expenses e ON e.id = bt.expense_id
        LEFT JOIN categories ec ON ec.id = e.category_id
        """;

    private const string SelectSql = $"{ProjectionSql}\nFROM bank_transactions bt\n{JoinsSql}";
    // RETURNING is the post-update row, not the statement's pre-update table snapshot.
    // PostgreSQL rechecks both guards after waiting for a concurrent updater.
    private const string ChangeReviewSql = $"""
        WITH updated AS (
            UPDATE bank_transactions SET review_status = :review_status
            WHERE id = :id AND expense_id IS NULL
              AND review_status IN ('needsReview', 'ignored')
            RETURNING *
        )
        {ProjectionSql}
        FROM updated bt
        {JoinsSql}
        """;

    private const string UpdateSuggestedCategorySql = """
        UPDATE bank_transactions
        SET suggested_category_id = :category_id
        WHERE sync_key = :sync_key
        """;

    public async Task<bool> InsertOrSkipAsync(BankTransactionInsert transaction, CancellationToken cancellationToken = default)
    {
        var result = await client.ExecuteAsync(
            InsertSql,
            ToParameters(transaction),
            cancellationToken: cancellationToken);
        // RETURNING id yields exactly one row for a fresh insert and none when the
        // sync_key conflicted — the explicit "new vs skipped" signal.
        return result.Rows.Count == 1;
    }

    public async Task<IReadOnlyList<BankTransactionDetail>> ListAsync(string reviewStatus = "all", CancellationToken cancellationToken = default)
    {
        var filter = reviewStatus == "all" ? "" : "\nWHERE bt.review_status = :review_status";
        var result = await ExecuteReviewAsync($"{SelectSql}{filter}\nORDER BY bt.booking_date DESC, bt.id DESC",
            [new("review_status", PostgresValue.FromString(reviewStatus))], cancellationToken);
        return result.Rows.Select(ToDetail).ToList();
    }

    public async Task<BankTransactionDetail> ChangeReviewStatusAsync(long id, MatchStatus matchStatus, CancellationToken cancellationToken = default)
    {
        var parameters = new PostgresParameter[]
        {
            new("id", PostgresValue.FromLong(id)),
            new("review_status", PostgresValue.FromString(ToReviewTarget(matchStatus)))
        };
        var result = await ExecuteReviewAsync(ChangeReviewSql, parameters, cancellationToken);
        if (result.Rows.Count != 0) return ToDetail(result.Rows.Single());
        var existing = await ExecuteReviewAsync($"{SelectSql}\nWHERE bt.id = :id",
            [new("id", PostgresValue.FromLong(id))], cancellationToken);
        throw existing.Rows.Count == 0
            ? new ProblemException(404, "Bank Transaction was not found.")
            : new ProblemException(409, "Bank Transaction is matched or no longer eligible for this review action.");
    }

    private const string MatchSql = $"""
        UPDATE bank_transactions bt SET expense_id = :expense_id, review_status = 'matched'
        WHERE bt.id = :id AND EXISTS (
            SELECT 1 FROM expenses candidate WHERE candidate.id = :expense_id AND {EligibilitySql})
        RETURNING bt.id
        """;
    private const string UnmatchSql = """
        UPDATE bank_transactions bt SET expense_id = NULL, review_status = 'needsReview'
        WHERE bt.id = :id AND bt.expense_id = :expense_id AND bt.review_status = 'matched'
        RETURNING bt.id
        """;

    public Task<BankTransactionDetail> MatchAsync(long id, int expenseId, CancellationToken cancellationToken = default) =>
        ChangeLinkAsync(id, expenseId, MatchSql, cancellationToken);

    public Task<BankTransactionDetail> UnmatchAsync(long id, int expenseId, CancellationToken cancellationToken = default) =>
        ChangeLinkAsync(id, expenseId, UnmatchSql, cancellationToken);

    private async Task<BankTransactionDetail> ChangeLinkAsync(long id, int expenseId, string sql, CancellationToken cancellationToken)
    {
        try
        {
            return await CommitLinkAsync(id, expenseId, sql, cancellationToken);
        }
        catch (Amazon.RDSDataService.AmazonRDSDataServiceException)
        {
            throw new ProblemException(502, "The Data API could not complete the Match operation. Read current state before retrying; a failed commit response may be outcome-unknown.");
        }
    }

    private async Task<BankTransactionDetail> CommitLinkAsync(long id, int expenseId, string sql, CancellationToken cancellationToken)
    {
        await using var transaction = unitOfWork;
        await transaction.BeginAsync(cancellationToken);
        await LockMatchTargetsAsync(id, expenseId, cancellationToken);
        var updated = await ExecuteReviewAsync(sql, LinkParameters(id, expenseId), cancellationToken);
        if (updated.Rows.Count != 1) throw new ProblemException(409, "Transaction or Expense is no longer eligible for this link action.");
        var result = await ReadAsync(id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task LockMatchTargetsAsync(long id, int expenseId, CancellationToken cancellationToken)
    {
        // Expense deletion takes the same Expense-before-Bank lock order.
        var expense = await ExecuteReviewAsync("SELECT id FROM expenses WHERE id = :id FOR UPDATE",
            [new("id", PostgresValue.FromLong(expenseId))], cancellationToken);
        if (expense.Rows.Count == 0) throw new ProblemException(404, "Expense was not found.");
        var bank = await ExecuteReviewAsync("SELECT id FROM bank_transactions WHERE id = :id FOR UPDATE",
            [new("id", PostgresValue.FromLong(id))], cancellationToken);
        if (bank.Rows.Count == 0) throw new ProblemException(404, "Bank Transaction was not found.");
    }

    private static IReadOnlyList<PostgresParameter> LinkParameters(long id, int expenseId) =>
        [new("id", PostgresValue.FromLong(id)), new("expense_id", PostgresValue.FromLong(expenseId))];

    private const string EligibilitySql = """
        bt.review_status = 'needsReview' AND bt.expense_id IS NULL
        AND bt.currency = 'EUR' AND candidate.amount = bt.amount
        AND (candidate.booking_date AT TIME ZONE 'Europe/Lisbon')::date
            BETWEEN bt.booking_date - 3 AND bt.booking_date + 3
        AND NOT EXISTS (SELECT 1 FROM bank_transactions linked WHERE linked.expense_id = candidate.id)
        """;

    public async Task<IReadOnlyList<BankLinkedExpense>> ListSuggestionsAsync(long id, CancellationToken cancellationToken = default)
    {
        var transaction = await ReadAsync(id, cancellationToken);
        var result = await ExecuteReviewAsync($"""
            SELECT candidate.id, CAST(candidate.booking_date AS text), CAST(candidate.amount AS text),
                   candidate.description, category.name
            FROM bank_transactions bt CROSS JOIN expenses candidate
            JOIN categories category ON category.id = candidate.category_id
            WHERE bt.id = :id AND {EligibilitySql}
            """, [new("id", PostgresValue.FromLong(id))], cancellationToken);
        return Services.BankTransactions.BankExpenseSuggestionRanking.Rank(transaction, result.Rows.Select(ToCandidate));
    }

    private async Task<BankTransactionDetail> ReadAsync(long id, CancellationToken cancellationToken)
    {
        var result = await ExecuteReviewAsync($"{SelectSql}\nWHERE bt.id = :id",
            [new("id", PostgresValue.FromLong(id))], cancellationToken);
        if (result.Rows.Count == 0) throw new ProblemException(404, "Bank Transaction was not found.");
        return ToDetail(result.Rows.Single());
    }

    private static BankLinkedExpense ToCandidate(IReadOnlyList<PostgresValue> row)
    {
        try
        {
            return new(checked((int)Id(row)), TimeService.InLisbon(DateTimeOffset.Parse(row[1].StringValue!, CultureInfo.InvariantCulture)),
                ExactDecimalJsonConverter.ParseExact(row[2].StringValue!), "EUR", row[3].StringValue!, row[4].StringValue!);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or FormatException or OverflowException)
        {
            throw new ProblemException(502, "Expense suggestions contain invalid exact data; no partial result was returned.");
        }
    }

    private static string ToReviewTarget(MatchStatus matchStatus) => matchStatus switch
    {
        MatchStatus.NeedsReview => "needsReview",
        MatchStatus.Ignored => "ignored",
        _ => throw new ArgumentOutOfRangeException(nameof(matchStatus), matchStatus,
            "Only NeedsReview and Ignored can be assigned without changing an Expense link.")
    };

    public Task UpdateSuggestedCategoryAsync(string syncKey, long? categoryId, CancellationToken cancellationToken = default) =>
        client.ExecuteAsync(
            UpdateSuggestedCategorySql,
            [
                // Typed write: the Data API's string-encoding quirk is READ-side only —
                // a string field into a bigint column fails live (DatabaseErrorException).
                new("category_id", categoryId is { } id ? PostgresValue.FromLong(id) : PostgresValue.Null()),
                new("sync_key", PostgresValue.FromString(syncKey)),
            ],
            cancellationToken: cancellationToken);

    private static IReadOnlyList<PostgresParameter> ToParameters(BankTransactionInsert transaction) =>
    [
        new("account_uid", PostgresValue.FromString(transaction.AccountUid)),
        new("booking_date", PostgresValue.FromString(transaction.BookingDate.ToString("yyyy-MM-dd"))),
        new("amount", PostgresValue.FromDouble((double)transaction.Amount)),
        new("currency", PostgresValue.FromString(transaction.Currency)),
        new("direction", PostgresValue.FromString(transaction.Direction)),
        new("remittance_information", transaction.RemittanceInformation is null
            ? PostgresValue.Null()
            : PostgresValue.FromString(transaction.RemittanceInformation)),
        new("status", PostgresValue.FromString(transaction.Status)),
        new("sync_key", PostgresValue.FromString(transaction.SyncKey)),
    ];

    private async Task<PostgresResult> ExecuteReviewAsync(string sql, IReadOnlyList<PostgresParameter> parameters, CancellationToken cancellationToken)
    {
        try
        {
            return await client.ExecuteAsync(sql, parameters, cancellationToken: cancellationToken);
        }
        catch (Amazon.RDSDataService.AmazonRDSDataServiceException)
        {
            throw new ProblemException(502, "The Data API could not complete the Bank review operation (provider failure or response limit). Read the current state before retrying a write.");
        }
    }

    private static BankTransactionDetail ToDetail(IReadOnlyList<PostgresValue> row)
    {
        try
        {
            if (row[9].StringValue is not ("needsReview" or "matched" or "ignored")
                || (row[9].StringValue == "matched") == row[10].IsNull)
                throw new ProblemException(502, "Bank review data has an incoherent review status or missing Expense link.");
            if (!row[10].IsNull && Enumerable.Range(11, 4).Any(column => row[column].StringValue is null))
                throw new ProblemException(502, "Bank review data has incomplete linked Expense details.");
            return MapDetail(row);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or FormatException or OverflowException)
        {
            throw new ProblemException(502, "Bank review data contains an invalid identifier, occurrence or exact amount; no partial result was returned.");
        }
    }

    private static BankTransactionDetail MapDetail(IReadOnlyList<PostgresValue> row) => new(
        Id: Id(row),
        AccountUid: row[1].StringValue!,
        BookingDate: DateOnly.Parse(row[2].StringValue!, CultureInfo.InvariantCulture),
        Amount: ExactDecimalJsonConverter.ParseExact(row[3].StringValue!),
        Currency: row[4].StringValue!,
        RemittanceInformation: row[5].IsNull ? null : row[5].StringValue,
        SuggestedCategory: row[6].IsNull
            ? null
            : new BankSuggestedCategory(Id(row, 6), row[7].StringValue!),
        Status: row[8].StringValue!,
        ReviewStatus: row[9].StringValue!,
        LinkedExpense: row[10].IsNull ? null : new BankLinkedExpense(
            checked((int)Id(row, 10)),
            TimeService.InLisbon(DateTimeOffset.Parse(row[11].StringValue!, CultureInfo.InvariantCulture)),
            ExactDecimalJsonConverter.ParseExact(row[12].StringValue!), "EUR", row[13].StringValue!, row[14].StringValue!),
        SuggestedExpenseCount: checked((int)Id(row, 15)));

    // The Data API returns bigint/numeric columns as string fields (observed live in
    // staging, issue #91) — accept either encoding, never assume one.
    private static long Id(IReadOnlyList<PostgresValue> row, int column = 0) =>
        row[column].StringValue is { } id
            ? long.Parse(id, CultureInfo.InvariantCulture)
            : row[column].LongValue!.Value;

}
