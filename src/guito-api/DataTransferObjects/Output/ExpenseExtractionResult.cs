namespace GuitoApi.DataTransferObjects.Output
{
    /// <summary>Raw fields extracted by the expense-extraction repository before service-side normalization.</summary>
    public class ExpenseExtractionResult
    {
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public string Description { get; set; } = string.Empty;
    }
}