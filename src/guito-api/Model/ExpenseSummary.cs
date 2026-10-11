namespace GuitoApi.Model;

public sealed class ExpenseSummary
{
    public int Id { get; init; }
    public DateTimeOffset? BookingDate { get; init; }
    public DateTimeOffset? CreationDate { get; init; }
    public DateTimeOffset? UpdateDate { get; init; }
    public string Currency { get; init; } = "EUR";
    public decimal? Amount { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? CreatorEmail { get; init; }
}
