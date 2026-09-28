namespace GuitoApi.Services
{
    /// <summary>
    /// Resolves the effective Sheets ranges for the current request. When the
    /// request carries <c>X-Sheet-Scope: smoke</c> (ADR-0009), reads/writes land
    /// on the environment's Smoke Test tab instead of the real tabs.
    /// </summary>
    public interface ISheetScopeResolver
    {
        bool IsSmokeScope { get; }

        string ExpensesRange { get; }

        string ExpensesDateRange { get; }

        string ExpensesLatestRange { get; }
    }
}
