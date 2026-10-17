using Amazon.RDSDataService;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Infrastructure.Postgres;

public sealed class DataApiPostgresBankExpenseCreationRepository(
    IPostgresDataApiClient client, IUnitOfWork unitOfWork, IExpenseRepository expenses,
    IBankTransactionRepository transactions) : IBankExpenseCreationRepository
{
    public async Task<BankTransactionDetail> CreateAsync(long id, ExpenseDraft draft, CancellationToken cancellationToken)
    {
        try
        {
            return await CommitCreationAsync(id, draft, cancellationToken);
        }
        catch (Exception exception) when (exception is AmazonRDSDataServiceException or HttpRequestException or TaskCanceledException or TimeoutException or IOException)
        {
            throw new ProblemException(502,
                "Data API create-and-match failed or commit outcome is unknown. Read current Bank state before deciding what to do next.");
        }
    }

    private async Task<BankTransactionDetail> CommitCreationAsync(long id, ExpenseDraft draft, CancellationToken cancellationToken)
    {
        await using var transaction = unitOfWork;
        await transaction.BeginAsync(cancellationToken);
        // The new Expense is private to this transaction; existing-Expense writers retain their lock order.
        await RequireBankAsync(id, cancellationToken);
        var expenseId = await expenses.CreateAsync(draft, cancellationToken);
        await LinkAsync(id, expenseId, cancellationToken);
        var result = await transactions.ReadAsync(id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

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

}
