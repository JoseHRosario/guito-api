using GuitoApi.Configuration;
using GuitoApi.Repositories;
using Microsoft.Extensions.Options;

namespace GuitoApi.Tests;

public class GoogleSheetsCategoryRepositoryTests
{
    [Fact]
    public async Task ListAsync_ShouldReturnCategoriesOrderedByName_WhenRowsExist()
    {
        // Arrange
        var options = TestSheetsConfiguration.Build();
        var repository = new GoogleSheetsCategoryRepository(
            Options.Create(options),
            new FakeGooglesheetsService(new FakeSheetsHttpHandler()));

        // Act
        var categories = await repository.ListAsync();

        // Assert
        Assert.Equal(["Groceries", "Restaurants", "Transport"], categories.Select(c => c.Name));
    }
}
