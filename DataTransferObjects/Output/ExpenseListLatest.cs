namespace GuitoApi.DataTransferObjects.Output
{
    public class ExpenseListLatest
    {
        public List<ExpenseListLatestDetail> Expenses { get; set; } = new List<ExpenseListLatestDetail>();
    }

    public class ExpenseListLatestDetail
    {
        public int StoredOrder { get; set; }
        public string? Date { get; set; }
        public string? Amount { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? CreatorEmail { get; set; }
    }
}
