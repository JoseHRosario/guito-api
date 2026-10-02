using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Exceptions;
using GuitoApi.Repositories;
using Microsoft.Extensions.Options;

namespace GuitoApi.Tests;

public class GoogleSheetsExpenseRepositoryTests
{
    private readonly FakeSheetsHttpHandler _handler = new();
    private readonly AppConfigurationOptions _options = TestSheetsConfiguration.Build();

    private GoogleSheetsExpenseRepository CreateRepository() =>
        new(Options.Create(_options),
            new FakeGooglesheetsService(_handler),
            new FakeUserIdentityResolver(),
            new FakeSheetScopeResolver(_options));

    private static ExpenseCreate AnExpense() => new()
    {
        Date = new DateTime(2026, 10, 2),
        Amount = 2.30m,
        Description = "morning coffee",
        Category = "Restaurants"
    };

    [Fact]
    public void CreateAsync_ShouldAppendRowWithDateAmountDescriptionCategoryAndCreator_WhenExpenseCreated()
    {
        var repository = CreateRepository();

        repository.CreateAsync(AnExpense()).GetAwaiter().GetResult();

        var body = _handler.AppendBodies.Last();
        Assert.Contains("\"2026-10-02\"", body);
        Assert.Contains("2.3", body);
        Assert.Contains("Morning Coffee", body);
        Assert.Contains("Restaurants", body);
        Assert.Contains(FakeUserIdentityResolver.Email, body);
        Assert.Equal(_options.Googlesheets.ExpensesRange, _handler.AppendRanges.Last());
    }

    [Fact]
    public void CreateAsync_ShouldBackfillYearMonthFormulasOnAppendedRow_WhenExpenseCreated()
    {
        var repository = CreateRepository();

        repository.CreateAsync(AnExpense()).GetAwaiter().GetResult();

        var updateRange = _handler.UpdateRanges.Last();
        var appendedRow = FakeSheetsHttpHandler.NextRowIndex.ToString();
        Assert.Contains(_options.Googlesheets.ExpensesDateRange + appendedRow, updateRange);
    }

    [Fact]
    public void CreateAsync_ShouldReturnOpaqueId_WhenExpenseCreated()
    {
        var repository = CreateRepository();

        var id = repository.CreateAsync(AnExpense()).GetAwaiter().GetResult();

        Assert.Equal(FakeSheetsHttpHandler.NextRowIndex.ToString(), id);
    }

    [Fact]
    public void ListLatestAsync_ShouldReadConfiguredLatestRangeDerivedFromAnchor_WhenLimitGiven()
    {
        var repository = CreateRepository();

        repository.ListLatestAsync(10).GetAwaiter().GetResult();

        // The fake's canned date column holds 3 rows from the anchor (row 5),
        // so the resolved last row is 7 and the latest range is clamped down.
        var (tab, firstRow, _) = ParseAnchor(_options.Googlesheets.ExpensesRange);
        var expected = string.Format(_options.Googlesheets.ExpensesLatestRange, firstRow, firstRow + 2);
        Assert.Contains(expected, _handler.ReadRanges);
    }

    [Fact]
    public void ListLatestAsync_ShouldReturnStoredOrderExpenses_WhenRowsExist()
    {
        var repository = CreateRepository();

        var expenses = repository.ListLatestAsync(10).GetAwaiter().GetResult();

        Assert.Equal(3, expenses.Count);
        Assert.Equal([1, 2, 3], expenses.Select(e => e.StoredOrder));
        Assert.Equal(12.50m, expenses[0].Amount);
        Assert.Equal("Coffee", expenses[0].Description);
        Assert.Equal(new DateTime(2026, 9, 20), expenses[0].Date);
        Assert.Equal("Restaurants", expenses[0].Category);
        Assert.Equal("user@example.com", expenses[0].CreatorEmail);
    }

    [Fact]
    public void DeleteAsync_ShouldDeleteOneRowDimensionOnSmokeTab_WhenRowExists()
    {
        _handler.SmokeRowExists = true;
        var repository = CreateRepository();
        var id = FakeSheetsHttpHandler.NextRowIndex.ToString();

        repository.DeleteAsync(id).GetAwaiter().GetResult();

        var deletion = _handler.DeleteDimensions.Last();
        Assert.Equal(FakeSheetsHttpHandler.SmokeSheetId, deletion.SheetId);
        Assert.Equal(FakeSheetsHttpHandler.NextRowIndex - 1, deletion.StartIndex);
        Assert.Equal(FakeSheetsHttpHandler.NextRowIndex, deletion.EndIndex);
        Assert.Contains($"Smoke Test!B{FakeSheetsHttpHandler.NextRowIndex}", _handler.ReadRanges);
    }

    [Fact]
    public async Task DeleteAsync_ShouldThrowExpenseNotFound_WhenRowAbsent()
    {
        _handler.SmokeRowExists = false;
        var repository = CreateRepository();

        var exception = await Assert.ThrowsAsync<ExpenseNotFoundException>(
            () => repository.DeleteAsync(FakeSheetsHttpHandler.NextRowIndex.ToString()));

        Assert.Contains(FakeSheetsHttpHandler.NextRowIndex.ToString(), exception.Message);
    }

    [Fact]
    public async Task DeleteAsync_ShouldThrowExpenseNotFound_WhenIdBeforeAnchor()
    {
        var repository = CreateRepository();
        var (_, firstDataRow, _) = ParseAnchor(_options.Googlesheets.ExpensesSmokeRange);

        await Assert.ThrowsAsync<ExpenseNotFoundException>(
            () => repository.DeleteAsync((firstDataRow - 1).ToString()));
    }

    private static (string Tab, int FirstDataRow, string Column) ParseAnchor(string range) =>
        range.Split('!', 2) is [var tab, var cell]
            ? (tab, int.Parse(new string(cell.Where(char.IsDigit).ToArray())), new string(cell.Where(char.IsLetter).ToArray()))
            : throw new ArgumentException(range);
}
