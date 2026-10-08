using System.Globalization;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Infrastructure.Postgres;

/// <summary>
/// IBankTransactionRepository over the Data API (issue #88, ADR-0013): raw SQL +
/// small mappers against the bank_transactions table, executed through
/// IPostgresDataApiClient (a statement inside an open unit of work joins its
/// transaction automatically — the repository never names a transaction id).
/// </summary>
public class DataApiPostgresBankTransactionRepository(IPostgresDataApiClient client) : IBankTransactionRepository
{
    private const string InsertSql = """
        INSERT INTO bank_transactions
            (account_uid, booking_date, amount, currency, direction, remittance_information, status, sync_key)
        VALUES
            (:account_uid, :booking_date::date, :amount, :currency, :direction, :remittance_information, :status, :sync_key)
        ON CONFLICT (sync_key) DO NOTHING
        RETURNING id
        """;

    private const string ListPendingSql = """
        SELECT bt.id, bt.account_uid, bt.booking_date, bt.amount, bt.currency,
               bt.remittance_information, c.id, c.name
        FROM bank_transactions bt
        LEFT JOIN categories c ON c.id = bt.suggested_category_id
        WHERE bt.expense_id IS NULL
        ORDER BY bt.booking_date DESC, bt.id DESC
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

    public async Task<IReadOnlyList<BankTransactionPendingDetail>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        var result = await client.ExecuteAsync(ListPendingSql, cancellationToken: cancellationToken);
        return result.Rows.Select(ToPendingDetail).ToList();
    }

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

    private static BankTransactionPendingDetail ToPendingDetail(IReadOnlyList<PostgresValue> row) => new(
        Id: Id(row),
        AccountUid: row[1].StringValue!,
        BookingDate: DateOnly.Parse(row[2].StringValue!),
        Amount: Amount(row),
        Currency: row[4].StringValue!,
        RemittanceInformation: row[5].IsNull ? null : row[5].StringValue,
        SuggestedCategory: row[6].IsNull
            ? null
            : new BankSuggestedCategory(Id(row, 6), row[7].StringValue!));

    // The Data API returns bigint/numeric columns as string fields (observed live in
    // staging, issue #91) — accept either encoding, never assume one.
    private static long Id(IReadOnlyList<PostgresValue> row, int column = 0) =>
        row[column].StringValue is { } id
            ? long.Parse(id, CultureInfo.InvariantCulture)
            : row[column].LongValue!.Value;

    private static decimal Amount(IReadOnlyList<PostgresValue> row) =>
        row[3].StringValue is { } amount
            ? decimal.Parse(amount, CultureInfo.InvariantCulture)
            : (decimal)row[3].DoubleValue!.Value;
}
