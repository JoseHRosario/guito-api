namespace GuitoApi.Model
{
    public class Expense
    {
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public string CreatorEmail { get; set; } = "";

    }
}
