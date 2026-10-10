namespace GuitoApi.Model;

public sealed class ExpenseDraft
{
    public DateTime Date { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "EUR";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";
    public string CreatorEmail { get; init; } = "";
}
