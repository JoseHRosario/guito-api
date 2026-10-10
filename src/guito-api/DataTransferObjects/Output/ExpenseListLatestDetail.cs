namespace GuitoApi.DataTransferObjects.Output
{
    public class ExpenseListLatestDetail
    {
        public string Id { get; set; } = "";
        public DateTimeOffset? OccurredAt { get; set; }
        public DateTimeOffset? CreationDate { get; set; }
        public DateTimeOffset? UpdateDate { get; set; }
        public string Currency { get; set; } = "EUR";
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? StoredOrder { get; set; }
        public DateTime? Date { get; set; }
        public decimal? Amount { get; set; }
        public string? AmountExact => Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? CreatorEmail { get; set; }
    }
}
