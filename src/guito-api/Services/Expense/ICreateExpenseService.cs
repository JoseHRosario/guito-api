using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.Expense
{
    public interface ICreateExpenseService
    {
        public Task<ExpenseCreated> CreateAsync(ExpenseCreate value, CancellationToken cancellationToken = default);
    }
}
