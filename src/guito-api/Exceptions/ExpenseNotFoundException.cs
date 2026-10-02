namespace GuitoApi.Exceptions
{
    /// <summary>
    /// A repository operation addressed an Expense Id that does not exist in the
    /// datastore (today: a stale or out-of-range sheet row index). The exception
    /// handler translates it to HTTP 404. Thrown by IExpenseRepository — the
    /// not-found contract is repository-level, not wire-level.
    /// </summary>
    public class ExpenseNotFoundException : Exception
    {
        public ExpenseNotFoundException(string id)
            : base($"Expense {id} not found")
        {
        }
    }
}
