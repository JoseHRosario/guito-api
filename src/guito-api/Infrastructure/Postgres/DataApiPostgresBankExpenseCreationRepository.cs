using System.Globalization;
using Amazon.RDSDataService;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Infrastructure.Postgres;

public sealed class DataApiPostgresBankExpenseCreationRepository(
    IPostgresDataApiClient client, IUnitOfWork unitOfWork, IExpenseRepository expenses) : IBankExpenseCreationRepository
{
    public async Task<BankCreationReceipt> CreateAsync(long id, Guid operationId, ExpenseDraft draft, CancellationToken cancellationToken)
    {
        try
        {
            var intent = NormalizeIntent(draft);
            var existing = await FindAsync(operationId, cancellationToken);
            if (existing is not null) return RequireSameIntent(existing, id, intent);
            var created = await CommitCreationAsync(id, operationId, draft, intent, cancellationToken);
            // A UUID conflict must be read only after the losing transaction has rolled back.
            return created ?? RequireSameIntent(await FindAsync(operationId, cancellationToken)
                ?? throw new ProblemException(502, "Conflicting operation receipt could not be recovered."), id, intent);
        }
        catch (Exception exception) when (exception is AmazonRDSDataServiceException or HttpRequestException or TaskCanceledException or TimeoutException or IOException)
        {
            throw ProviderFailure();
        }
    }

    private async Task<BankCreationReceipt?> CommitCreationAsync(long id, Guid operationId, ExpenseDraft draft,
        string intent, CancellationToken cancellationToken)
    {
        await using var transaction = unitOfWork;
        await transaction.BeginAsync(cancellationToken);
        await RequireBankAsync(id, cancellationToken);
        var existing = await FindAsync(operationId, cancellationToken);
        if (existing is not null) return RequireSameIntent(existing, id, intent);
        var expenseId = await expenses.CreateAsync(draft, cancellationToken);
        await LinkAsync(id, expenseId, cancellationToken);
        if (!await InsertReceiptAsync(id, operationId, expenseId, intent, cancellationToken)) return null;
        await transaction.CommitAsync(cancellationToken);
        return new(operationId, id, expenseId);
    }

    private async Task<bool> InsertReceiptAsync(long id, Guid operationId, int expenseId, string intent, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteAsync("""
            INSERT INTO bank_expense_creation_receipts (operation_id, transaction_id, expense_id, intent)
            VALUES (CAST(:operation_id AS uuid), :id, :expense_id, :intent)
            ON CONFLICT (operation_id) DO NOTHING
            RETURNING operation_id
            """, [Text("operation_id", operationId.ToString("D")), new("id", PostgresValue.FromLong(id)),
                new("expense_id", PostgresValue.FromLong(expenseId)), Text("intent", intent)], cancellationToken: cancellationToken);
        return result.Rows.Count == 1;
    }

    private static string NormalizeIntent(ExpenseDraft draft) => System.Text.Json.JsonSerializer.Serialize(new[]
    {
        draft.Amount.ToString("G29", CultureInfo.InvariantCulture),
        draft.BookingDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        draft.Description, draft.Category, draft.Currency
    });

    private static BankCreationReceipt RequireSameIntent(StoredBankCreationReceipt existing, long id, string intent) =>
        existing.Receipt.TransactionId == id && existing.Intent == intent ? existing.Receipt
            : throw new ProblemException(409, "operationId has already been used with a different creation intent.");

    private async Task RequireBankAsync(long id, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteAsync("SELECT id FROM bank_transactions WHERE id = :id FOR UPDATE",
            [new("id", PostgresValue.FromLong(id))], cancellationToken: cancellationToken);
        if (result.Rows.Count == 0) throw new ProblemException(404, "Bank Transaction was not found.");
    }

    private async Task LinkAsync(long id, int expenseId, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteAsync("""
            UPDATE bank_transactions bt SET expense_id = :expense_id, review_status = 'matched'
            WHERE bt.id = :id AND bt.expense_id IS NULL AND bt.review_status = 'needsReview'
              AND bt.currency = 'EUR' AND bt.status = 'BOOK' AND bt.direction = 'DBIT' AND bt.amount > 0
              AND bt.amount::text NOT IN ('NaN', 'Infinity', '-Infinity')
              AND bt.booking_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'
              AND NOT EXISTS (SELECT 1 FROM bank_transactions linked WHERE linked.expense_id = :expense_id)
            RETURNING bt.id
            """, [new("id", PostgresValue.FromLong(id)), new("expense_id", PostgresValue.FromLong(expenseId))],
            cancellationToken: cancellationToken);
        if (result.Rows.Count != 1) throw new ProblemException(409, "Bank Transaction is no longer eligible for creation and linking.");
    }

    public async Task<BankCreationReceipt?> ReadAsync(long id, Guid operationId, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await FindAsync(operationId, cancellationToken);
            return existing?.Receipt.TransactionId == id ? existing.Receipt : null;
        }
        catch (Exception exception) when (exception is AmazonRDSDataServiceException or HttpRequestException or TaskCanceledException or TimeoutException or IOException)
        {
            throw ProviderFailure();
        }
    }

    private async Task<StoredBankCreationReceipt?> FindAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteAsync("""
            SELECT CAST(transaction_id AS text), CAST(expense_id AS text), intent
            FROM bank_expense_creation_receipts WHERE operation_id = CAST(:operation_id AS uuid)
            """, [Text("operation_id", operationId.ToString("D"))], cancellationToken: cancellationToken);
        if (result.Rows.Count == 0) return null;
        var row = result.Rows.Single();
        return new(new(operationId, long.Parse(row[0].StringValue!, CultureInfo.InvariantCulture),
            int.Parse(row[1].StringValue!, CultureInfo.InvariantCulture)), row[2].StringValue!);
    }

    private static PostgresParameter Text(string name, string value) => new(name, PostgresValue.FromString(value));

    private static ProblemException ProviderFailure() => new(502,
        "Data API create-and-match failed or commit outcome is unknown. GET the operation receipt or retry the identical operationId and intent; do not use a new UUID.");
}
