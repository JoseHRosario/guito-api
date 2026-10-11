using GuitoApi.Infrastructure.Serialization;
using System.Text.Json.Serialization;

namespace GuitoApi.DataTransferObjects.Output
{
    public class ExpenseExtracted
    {
        public DateTime Date { get; set; }
        [JsonConverter(typeof(ExactDecimalJsonConverter))]
        public decimal Amount { get; set; }
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
    }
}
