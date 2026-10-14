using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GuitoApi.Tests;

public class PostgresCategoryBoundaryTests
{
    [Fact]
    public async Task Category_ShouldReturnEmptyNameList_WhenPostgresHasNoCategories()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        factory.Postgres.CategoryRows.Clear();
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync("/Category");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"categories\":[]}", await response.Content.ReadAsStringAsync());
        Assert.Single(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task CreateExpense_ShouldRejectUnknownCategory_WhenPostgresHasNoCategories()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        factory.Postgres.PersistExpenses = true;
        factory.Postgres.CategoryRows.Clear();
        using var client = factory.CreateClient();

        // Act
        using var categories = await client.GetAsync("/Category");
        using var response = await client.PostAsJsonAsync("/Expense", new
        {
            bookingDate = "2026-07-02", amount = "2.30", category = "Restaurants"
        });

        // Assert
        Assert.Equal("{\"categories\":[]}", await categories.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain(factory.Postgres.ExecutedSql, sql => sql.StartsWith("INSERT INTO expenses", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Extract_ShouldLeaveCategoryForManualChoice_WhenPostgresHasNoCategories()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        factory.Postgres.CategoryRows.Clear();
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync("/AI/extract", new { prompt = "coffee 2.30" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", body.GetProperty("category").GetString());
        Assert.Single(factory.OpenRouterHandler.DecisionRequestBodies); // Expense gate only, no category decision.
        Assert.Single(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task Extract_ShouldUsePostgresNamesAsCandidates_WhenCategoriesExist()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        factory.Postgres.CategoryRows.Clear();
        factory.Postgres.CategoryRows.Add((2147483648L, "Database only", "Private description"));
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync("/AI/extract", new { prompt = "coffee 2.30" });
        var listed = await client.GetFromJsonAsync<JsonElement>("/Category");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var decision = JsonDocument.Parse(factory.OpenRouterHandler.DecisionRequestBodies.Last());
        var candidate = Assert.Single(decision.RootElement.GetProperty("questions").GetProperty("category").GetProperty("criteria").EnumerateObject());
        Assert.Equal("Database only", candidate.Name);
        Assert.Equal("Database only", candidate.Value.GetString());
        Assert.Equal("{\"categories\":[{\"name\":\"Database only\"}]}", listed.GetRawText());
        Assert.Equal(2, factory.Postgres.ExecutedSql.Count);
    }

    [Fact]
    public async Task CreateExpense_ShouldAcceptListedCategory_WhenPostgresContainsCategory()
    {
        // Arrange
        using var factory = CreateFactoryWithSingleCategory();
        using var client = factory.CreateClient();

        // Act
        var categories = await client.GetFromJsonAsync<JsonElement>("/Category");
        var listedName = Assert.Single(categories.GetProperty("categories").EnumerateArray()).GetProperty("name").GetString();
        using var response = await client.PostAsJsonAsync("/Expense", new
        {
            bookingDate = "2026-07-02", amount = "2.30", category = listedName
        });
        var latest = await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1");

        // Assert
        Assert.Equal("Database only", listedName);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Database only", Assert.Single(latest.GetProperty("expenses").EnumerateArray()).GetProperty("category").GetString());
        AssertInsertedCategoryIdentity(factory);
    }

    private static CustomWebApplicationFactory CreateFactoryWithSingleCategory()
    {
        var factory = new CustomWebApplicationFactory();
        factory.Postgres.PersistExpenses = true;
        factory.Postgres.CategoryRows.Clear();
        factory.Postgres.CategoryRows.Add((2147483648L, "Database only", "Private description"));
        return factory;
    }

    private static void AssertInsertedCategoryIdentity(CustomWebApplicationFactory factory)
    {
        var insertIndex = factory.Postgres.ExecutedSql.FindIndex(sql => sql.StartsWith("INSERT INTO expenses", StringComparison.Ordinal));
        Assert.True(insertIndex >= 0);
        Assert.Equal(2147483648L, factory.Postgres.ExecutedParameters[insertIndex]!.Single(parameter => parameter.Name == "category_id").Value.LongValue);
    }

    [Fact]
    public async Task Category_ShouldReturnAlphabeticalNamesOnly_WhenPostgresContainsCategories()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        factory.Postgres.CategoryRows.Clear();
        factory.Postgres.CategoryRows.Add((9, "Zulu", null));
        factory.Postgres.CategoryRows.Add((2147483648L, "Alpha", "Private description"));
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync("/Category");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"categories\":[{\"name\":\"Alpha\"},{\"name\":\"Zulu\"}]}", body.GetRawText());
        Assert.Equal("SELECT id, name, description FROM categories ORDER BY name", Assert.Single(factory.Postgres.ExecutedSql));
    }
}
