namespace GuitoApi.DataTransferObjects.Output
{
    /// <summary>
    /// The API's response to POST /expense. Exposes the Expense's integer Id — a
    /// sheet row index or database key (ADR 0015); callers must not
    /// interpret it as a storage mechanism.
    /// </summary>
    public class ExpenseCreated
    {
        public int Id { get; set; }
    }
}
