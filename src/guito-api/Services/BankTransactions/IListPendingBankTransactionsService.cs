using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankTransactions;

/// <summary>
/// GET /BankTransaction (issue #91): returns the pending (unmatched,
/// expense_id IS NULL) bank transactions, newest bookings first — the
/// matching screen's (#83) candidate feed. A thin adapter over
/// IBankTransactionRepository.ListPendingAsync.
/// </summary>
public interface IListPendingBankTransactionsService
{
    Task<IReadOnlyList<BankTransactionPending>> ListPendingAsync(CancellationToken cancellationToken = default);
}
