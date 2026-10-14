namespace GuitoApi.DataTransferObjects.Output
{
    /// <summary>
    /// The API's response to POST /expense. Exposes the Expense's integer Id — a
    /// database-generated identity; callers must not
    /// interpret it as a storage mechanism.
    /// </summary>
    public class ExpenseCreated
    {
        public int Id { get; set; }
    }
}
