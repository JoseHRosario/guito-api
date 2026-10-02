using GuitoApi.Configuration;
using GuitoApi.Repositories;
using Microsoft.Extensions.Options;

namespace GuitoApi.Tests;

public class GoogleSheetsCategoryRepositoryTests
{
    private readonly FakeSheetsHttpHandler _handler = new();
    private readonly GuitoApi.Configuration.AppConfigurationOptions _options = TestSheetsConfiguration.Build();

    [Fact]
    public void ListAsync_ShouldReturnCategoriesOrderedByName_WhenRowsExist()
    {
        var repository = new GuitoApi.Repositories.GoogleSheetsCategoryRepository(
            Options.Create(_options),
            new FakeGooglesheetsService(_handler));

        var categories = repository.ListAsync().GetAwaiter().GetResult();

        Assert.Equal(["Groceries", "Restaurants", "Transport"], categories.Select(c => c.Name));
    }
}
