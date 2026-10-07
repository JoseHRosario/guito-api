using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
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
            (:account_uid, :booking_date, :amount, :currency, :direction, :remittance_information, :status, :sync_key)
        ON CONFLICT (sync_key) DO NOTHING
        RETURNING id
        """;

    private const string ListPendingSql = """
        SELECT id, account_uid, booking_date, amount, currency, remittance_information
        FROM bank_transactions
        WHERE expense_id IS NULL
        ORDER BY booking_date DESC, id DESC
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
        Id: row[0].LongValue!.Value,
        AccountUid: row[1].StringValue!,
        BookingDate: DateOnly.Parse(row[2].StringValue!),
        Amount: (decimal)row[3].DoubleValue!.Value,
        Currency: row[4].StringValue!,
        RemittanceInformation: row[5].IsNull ? null : row[5].StringValue);
}