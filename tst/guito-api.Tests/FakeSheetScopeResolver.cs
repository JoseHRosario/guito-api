using GuitoApi.Configuration;
using GuitoApi.Services;

namespace GuitoApi.Tests;

/// <summary>
/// Non-smoke scope resolver for direct repository unit tests: serves the real
/// (non-smoke) ranges from the provided configuration, derived from the same
/// options instance the test asserts against — no hardcoded tab names.
/// </summary>
public class FakeSheetScopeResolver : ISheetScopeResolver
{
    private readonly AppConfigurationOptions _options;

    public FakeSheetScopeResolver(AppConfigurationOptions options) => _options = options;

    public bool IsSmokeScope => false;

    public string ExpensesRange => _options.Googlesheets.ExpensesRange;

    public string ExpensesDateRange => _options.Googlesheets.ExpensesDateRange;

    public string ExpensesLatestRange => _options.Googlesheets.ExpensesLatestRange;
}
