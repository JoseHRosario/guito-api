namespace GuitoApi.DataTransferObjects.Output
{
    public class ExpenseExtracted
    {
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public string AmountExact => Amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
    }
}
