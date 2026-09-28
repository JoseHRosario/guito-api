namespace GuitoApi.Services.Expense
{
    /// <summary>
    /// Removes one expense row by id (today = sheet row index, the opaque Expense
    /// Id per ADR-0009). The implementing service pins smoke scope: it resolves
    /// ranges from the configured smoke tab, never from the request scope header,
    /// so the delete route cannot be pointed at a real tab.
    /// </summary>
    public interface IDeleteExpenseRowService
    {
        Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    }
}
