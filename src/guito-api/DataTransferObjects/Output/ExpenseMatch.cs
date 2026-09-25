namespace GuitoApi.DataTransferObjects.Output
{
    public class ExpenseMatch
    {
        public DateTime? Date { get; set; }
        public ExpenseMatchDetail? Expense { get; set; }
        public TransactionMatchDetail? Transaction { get; set; }
    }
}
