namespace GuitoApi.DataTransferObjects.Output
{
    /// <summary>
    /// The API's response to POST /expense. Exposes the Expense's opaque Id — today
    /// the sheet row index, later a database key (ADR 0009); callers must not
    /// interpret it as a storage mechanism.
    /// </summary>
    public class ExpenseCreated
    {
        public int Id { get; set; }
    }
}
