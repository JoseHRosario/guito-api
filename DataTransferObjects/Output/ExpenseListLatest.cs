namespace GuitoApi.DataTransferObjects.Output
{
    public class ExpenseListLatest
    {
        public List<ExpenseListLatestDetail> Expenses { get; set; } = new List<ExpenseListLatestDetail>();
    }

    public class ExpenseListLatestDetail
    {
        public int StoredOrder { get; set; }
        public DateTime? Date { get; set; }
        public decimal? Amount { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? CreatorEmail { get; set; }
    }
}
