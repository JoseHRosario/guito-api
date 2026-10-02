using GuitoApi.Configuration;

namespace GuitoApi.Tests;

/// <summary>
/// Test spreadsheet configuration matching the fake handler's canned responses
/// (values from appsettings), plus shared derivations from those config values.
/// </summary>
public static class TestSheetsConfiguration
{
    public static AppConfigurationOptions Build() => new()
    {
        Googlesheets = new Googlesheets
        {
            SpreadsheetId = "test-sheet",
            ExpensesRange = "Expenses!B5",
            ExpensesDateRange = "Expenses!C",
            ExpensesLatestRange = "Expenses!B{0}:H{1}",
            ExpensesSmokeRange = "Smoke Test!B5",
            ExpensesDateSmokeRange = "Smoke Test!C",
            ExpensesLatestSmokeRange = "Smoke Test!B{0}:H{1}",
            CategoriesRange = "Config!D2:D24"
        }
    };

    /// <summary>First data row parsed from a config range anchor (the digits after the cell column).</summary>
    public static int FirstDataRow(AppConfigurationOptions options)
    {
        var anchor = options.Googlesheets.ExpensesRange.Split('!', 2)[1];
        return int.Parse(new string(anchor.Where(char.IsDigit).ToArray()));
    }
}
