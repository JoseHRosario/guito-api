using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Services.Account;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace GuitoApi.Services.Expense
{
    public class MatchExpensesService : IMatchExpensesService
    {
        private readonly IListTransactionsService _listTransactionsService;
        private readonly IListLatestExpensesService _listLatestExpensesService;
        private const decimal Deviation = 0.5m;

        public MatchExpensesService(IListTransactionsService listTransactionsService,
            IListLatestExpensesService listLatestExpensesService)
        {
            _listTransactionsService = listTransactionsService;
            _listLatestExpensesService = listLatestExpensesService;
        }

        public async Task<ExpenseMatchList> MatchExpenses()
        {
            var output = new ExpenseMatchList();
            var transactionsTask = _listTransactionsService.List();
            var expensesTask = _listLatestExpensesService.ListLatest(10);
            await Task.WhenAll(transactionsTask, expensesTask);
            var transactions = transactionsTask.Result.Transactions.OrderByDescending(x => x.Date);
            var expenses = expensesTask.Result.Expenses.OrderByDescending(x => x.Date);
            foreach (var transaction in transactions)
            {
                var matchingExpense = expenses
                    .FirstOrDefault(x => x.Amount >= GetAmountLowerLimit(transaction.Amount)
                    && x.Amount <= GetAmountUpperLimit(transaction.Amount)
                    && x.Date == transaction.Date);

                var match = new ExpenseMatch
                {
                    Date = transaction.Date
                };
                match.Transaction = new TransactionMatchDetail
                {
                    Id = transaction.Id,
                    Amount = transaction.Amount,
                    Description = transaction.Description
                };

                if (matchingExpense != null)
                {
                    match.Expense = new ExpenseMatchDetail
                    {
                        Amount = matchingExpense.Amount,
                        Description = matchingExpense.Description
                    };
                }

                output.Matches.Add(match);
            }

            // Add all expenses that didn't match
            foreach (var expense in expenses)
            {
                if (output.Matches.Any(x => x?.Date == expense.Date 
                                    && x?.Expense?.Amount >= GetAmountLowerLimit(expense.Amount)
                                    && x?.Expense?.Amount <= GetAmountUpperLimit(expense.Amount)))
                    continue;

                var match = new ExpenseMatch
                {
                    Date = expense.Date,
                    Expense = new ExpenseMatchDetail
                    {
                        Amount = expense.Amount,
                        Description = expense.Description
                    }
                };

                output.Matches.Add(match);
            }

            return output;
        }

        private decimal GetAmountLowerLimit(decimal? amount)
        {
            if (amount == null)
                return 999999999;

            return amount.Value - Deviation;
        }

        private decimal GetAmountUpperLimit(decimal? amount)
        {
            if (amount == null)
                return -999999999;

            return amount.Value + Deviation;
        }
    }
}
