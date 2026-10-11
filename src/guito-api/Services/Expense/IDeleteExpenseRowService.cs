namespace GuitoApi.Services.Expense
{
    /// <summary>
    /// Removes one smoke Expense by integer identity. The repository pins
    /// smoke scope independently of the request header, never normal records.
    /// </summary>
    public interface IDeleteExpenseRowService
    {
        Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    }
}
