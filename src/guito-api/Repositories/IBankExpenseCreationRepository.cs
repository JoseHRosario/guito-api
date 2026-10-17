using GuitoApi.Model;

namespace GuitoApi.Repositories;

public interface IBankExpenseCreationRepository
{
    Task<BankTransactionDetail> CreateAsync(long id, ExpenseDraft draft, CancellationToken cancellationToken);
}
