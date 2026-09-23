using GuitoApi.DataTransferObjects.Input;

namespace GuitoApi.Services.Expense
{
    public interface ICreateExpenseService
    {
        public Task Create(ExpenseCreate value);
    }
}
