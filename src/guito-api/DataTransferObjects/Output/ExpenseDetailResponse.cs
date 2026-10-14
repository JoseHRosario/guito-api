namespace GuitoApi.DataTransferObjects.Output;

public sealed record ExpenseDetailResponse(int Id, string Amount, string Description, string Category,
    DateTimeOffset BookingDate, string CreatorEmail, DateTimeOffset CreationDate,
    DateTimeOffset? UpdateDate, string Revision)
{
    public string Currency { get; init; } = "EUR";
}
