using GuitoApi.Model;

namespace GuitoApi.Repositories;

public interface IBankExpenseCreationRepository
{
    Task<BankCreationReceipt> CreateAsync(long id, Guid operationId, ExpenseDraft draft, CancellationToken cancellationToken);
    Task<BankCreationReceipt?> ReadAsync(long id, Guid operationId, CancellationToken cancellationToken);
}
