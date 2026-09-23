namespace GuitoApi.DataTransferObjects.Output
{
    public class ExpenseMatchList
    {
        public List<ExpenseMatch> Matches { get; set; } = new List<ExpenseMatch>();
    }

    public class ExpenseMatch
    {
        public DateTime? Date { get; set; }
        public ExpenseMatchDetail? Expense { get; set; }
        public TransactionMatchDetail? Transaction { get; set; }
    }

    public class ExpenseMatchDetail
    {
        public decimal? Amount { get; set; }
        public string? Description { get; set; }
    }

    public class TransactionMatchDetail
    {
        public string? Id { get; set; }
        public decimal? Amount { get; set; }
        public string? Description { get; set; }
    }
}
