using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.Expense
{
    public interface IListLatestExpensesService
    {
        public Task<ExpenseListLatest> ListLatest(int count);
    }
}
