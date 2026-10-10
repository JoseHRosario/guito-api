namespace GuitoApi.Services.Expense
{
    /// <summary>
    /// Removes one smoke Expense by opaque string identity. The repository pins
    /// smoke scope independently of the request header, never normal records.
    /// </summary>
    public interface IDeleteExpenseRowService
    {
        Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    }
}
