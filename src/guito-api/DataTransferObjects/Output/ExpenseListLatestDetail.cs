using GuitoApi.Infrastructure.Serialization;
using System.Text.Json.Serialization;

namespace GuitoApi.DataTransferObjects.Output
{
    public class ExpenseListLatestDetail
    {
        public int Id { get; set; }
        public DateTimeOffset? BookingDate { get; set; }
        public DateTimeOffset? CreationDate { get; set; }
        public DateTimeOffset? UpdateDate { get; set; }
        public string Currency { get; set; } = "EUR";
        [JsonConverter(typeof(ExactDecimalJsonConverter))]
        public decimal? Amount { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? CreatorEmail { get; set; }
    }
}
