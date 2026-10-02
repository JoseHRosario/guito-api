using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.Sheets;
using GuitoApi.Repositories;
using Microsoft.Extensions.Options;

namespace GuitoApi.Tests;

public class GoogleSheetsExpenseRepositoryTests
{
    private readonly FakeSheetsHttpHandler _handler = new();
    private readonly AppConfigurationOptions _options = TestSheetsConfiguration.Build();

    private GoogleSheetsExpenseRepository CreateRepository() =>
        new(Options.Create(_options),
            new FakeGooglesheetsClientProvider(_handler),
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
    public async Task CreateAsync_ShouldAppendRowWithDateAmountDescriptionCategoryAndCreator_WhenExpenseCreated()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        await repository.CreateAsync(AnExpense());

        // Assert
        var body = _handler.AppendBodies.Last();
        Assert.Contains("\"2026-10-02\"", body);
        Assert.Contains("2.3", body);
        Assert.Contains("Morning Coffee", body);
        Assert.Contains("Restaurants", body);
        Assert.Contains(FakeUserIdentityResolver.Email, body);
        Assert.Equal(_options.Googlesheets.ExpensesRange, _handler.AppendRanges.Last());
    }

    [Fact]
    public async Task CreateAsync_ShouldBackfillYearMonthFormulasOnAppendedRow_WhenExpenseCreated()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        await repository.CreateAsync(AnExpense());

        // Assert
        var updateRange = _handler.UpdateRanges.Last();
        Assert.Contains(_options.Googlesheets.ExpensesDateRange + FakeSheetsHttpHandler.NextRowIndex, updateRange);
    }

    [Fact]
    public async Task CreateAsync_ShouldReturnOpaqueId_WhenExpenseCreated()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var id = await repository.CreateAsync(AnExpense());

        // Assert
        Assert.Equal(FakeSheetsHttpHandler.NextRowIndex.ToString(), id);
    }

    [Fact]
    public async Task ListLatestAsync_ShouldReadConfiguredLatestRangeDerivedFromAnchor_WhenLimitGiven()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        await repository.ListLatestAsync(10);

        // Assert
        // The fake's canned date column holds 3 rows from the configured anchor,
        // so the resolved last row is anchor + 2 and the range is clamped to it.
        var firstDataRow = TestSheetsConfiguration.FirstDataRow(_options);
        var expected = string.Format(_options.Googlesheets.ExpensesLatestRange, firstDataRow, firstDataRow + 2);
        Assert.Contains(expected, _handler.ReadRanges);
    }

    [Fact]
    public async Task ListLatestAsync_ShouldReturnStoredOrderExpenses_WhenRowsExist()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var expenses = await repository.ListLatestAsync(10);

        // Assert
        Assert.Equal(3, expenses.Count);
        Assert.Equal([1, 2, 3], expenses.Select(e => e.StoredOrder));
        Assert.Equal(12.50m, expenses[0].Amount);
        Assert.Equal("Coffee", expenses[0].Description);
        Assert.Equal(new DateTime(2026, 9, 20), expenses[0].Date);
        Assert.Equal("Restaurants", expenses[0].Category);
        Assert.Equal("user@example.com", expenses[0].CreatorEmail);
    }

    [Fact]
    public async Task DeleteAsync_ShouldDeleteOneRowDimensionOnSmokeTab_WhenRowExists()
    {
        // Arrange
        _handler.SmokeRowExists = true;
        var repository = CreateRepository();
        var id = FakeSheetsHttpHandler.NextRowIndex.ToString();

        // Act
        await repository.DeleteAsync(id);

        // Assert
        var deletion = _handler.DeleteDimensions.Last();
        Assert.Equal(FakeSheetsHttpHandler.SmokeSheetId, deletion.SheetId);
        Assert.Equal(FakeSheetsHttpHandler.NextRowIndex - 1, deletion.StartIndex);
        Assert.Equal(FakeSheetsHttpHandler.NextRowIndex, deletion.EndIndex);
        Assert.Contains($"Smoke Test!B{FakeSheetsHttpHandler.NextRowIndex}", _handler.ReadRanges);
    }

    [Fact]
    public async Task DeleteAsync_ShouldThrowExpenseNotFound_WhenRowAbsent()
    {
        // Arrange
        _handler.SmokeRowExists = false;
        var repository = CreateRepository();

        // Act
        var exception = await Assert.ThrowsAsync<ExpenseNotFoundException>(
            () => repository.DeleteAsync(FakeSheetsHttpHandler.NextRowIndex.ToString()));

        // Assert
        Assert.Contains(FakeSheetsHttpHandler.NextRowIndex.ToString(), exception.Message);
    }

    [Fact]
    public async Task DeleteAsync_ShouldThrowExpenseNotFound_WhenIdBeforeAnchor()
    {
        // Arrange
        var repository = CreateRepository();
        var firstDataRow = TestSheetsConfiguration.FirstDataRow(_options);

        // Act
        var exception = await Assert.ThrowsAsync<ExpenseNotFoundException>(
            () => repository.DeleteAsync((firstDataRow - 1).ToString()));

        // Assert
        Assert.Equal($"Expense {firstDataRow - 1} not found", exception.Message);
    }
}
