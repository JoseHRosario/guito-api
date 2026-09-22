using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.Expense
{
    public interface IMatchExpensesService
    {
        public Task<ExpenseMatchList> MatchExpenses();
    }
}
