namespace GuitoApi.Model;

public sealed class ExpenseSummary
{
    public string Id { get; init; } = "";
    public int StoredOrder { get; init; }
    public DateTime? Date { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public DateTimeOffset? CreationDate { get; init; }
    public DateTimeOffset? UpdateDate { get; init; }
    public string Currency { get; init; } = "EUR";
    public decimal? Amount { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? CreatorEmail { get; init; }
}
