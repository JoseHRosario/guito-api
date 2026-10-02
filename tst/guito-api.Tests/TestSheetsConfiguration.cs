using GuitoApi.Configuration;

namespace GuitoApi.Tests;

/// <summary>Test spreadsheet configuration matching the fake handler's canned responses (values from appsettings).</summary>
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
}
