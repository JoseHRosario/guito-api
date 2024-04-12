namespace GuitoApi.DataTransferObjects.Output
{
    public class TransactionList
    {
        public List<TransactionListDetail> Transactions { get; set; } = new List<TransactionListDetail>();
    }

    public class TransactionListDetail
    {
        public string? Id { get; set; }
        public DateTime? Date { get; set; }
        public decimal? Amount { get; set; }
        public string? Description { get; set; }
    }
}
