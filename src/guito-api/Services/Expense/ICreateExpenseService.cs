using GuitoApi.DataTransferObjects.Input;

namespace GuitoApi.Services.Expense
{
    public interface ICreateExpenseService
    {
        public Task CreateAsync(ExpenseCreate value, CancellationToken cancellationToken = default);
    }
}
