using System.Net;
using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Services.Expense;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

public class ExpenseSynchronousScopeDisposalTests
{
    [Theory]
    [InlineData("/Expense/7")]
    [InlineData("/Expense/latest/1")]
    [InlineData("/Expense/history")]
    [InlineData("/warm")]
    public async Task Http_ShouldCompleteWithoutScopeDisposalFailure_WhenLambdaDisposesRequestSynchronouslyAsync(string route)
    {
        // Arrange
        using var factory = CreateFactory();
        using var host = factory.WithWebHostBuilder(builder => Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(
            builder, services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, SynchronousRequestScopeStartupFilter>()));
        using var client = host.CreateClient();
        // Act
        using var response = await client.GetAsync(route);
        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, factory.Postgres.Commits);
        Assert.Equal(0, factory.Postgres.Rollbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scope_ShouldDisposeSynchronouslyWithoutLeaks_WhenExpenseCompositionIsResolvedAsync(bool abandonTransaction)
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var before = await client.GetStringAsync("/Expense/7");
        var scope = factory.Services.CreateScope();
        var detail = scope.ServiceProvider.GetRequiredService<IReadExpenseDetailService>();
        var context = scope.ServiceProvider.GetRequiredService<PostgresTransactionContext>();
        if (abandonTransaction) await BeginAbandonedWriteAsync(scope);
        // Act
        var error = Record.Exception(scope.Dispose);
        // Assert
        Assert.Null(error);
        await AssertDisposedAsync(factory, client, context, before, abandonTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scope_ShouldSurfaceRollbackFailureAndRetainOpenContext_WhenProviderRejectsDisposalAsync(bool synchronous)
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var scope = factory.Services.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var context = scope.ServiceProvider.GetRequiredService<PostgresTransactionContext>();
        await unit.BeginAsync();
        var failure = new InvalidOperationException("Rollback transport failed.");
        factory.Postgres.RollbackException = failure;
        // Act
        var error = synchronous ? Record.Exception(scope.Dispose) : await Record.ExceptionAsync(async () => await scope.DisposeAsync());
        // Assert
        await AssertFailedRollbackAsync(factory, unit, context, failure, error);
    }

    [Fact]
    public async Task Scope_ShouldWaitForRollbackCompletion_WhenDisposedSynchronouslyAsync()
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Postgres.RollbackCompletion = completion.Task;
        // Act
        var disposal = Task.Run(scope.Dispose);
        await factory.Postgres.RollbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completedBeforeRollback = disposal.IsCompleted;
        completion.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        // Assert
        Assert.False(completedBeforeRollback);
        Assert.Equal(1, factory.Postgres.Rollbacks);
        Assert.Equal(0, factory.Postgres.Commits);
    }

    private static async Task AssertFailedRollbackAsync(CustomWebApplicationFactory factory, IUnitOfWork unit,
        PostgresTransactionContext context, Exception expected, Exception? actual)
    {
        Assert.Same(expected, actual);
        Assert.True(context.IsOpen);
        Assert.Equal(0, factory.Postgres.Rollbacks);
        Assert.Equal(0, factory.Postgres.Commits);
        factory.Postgres.RollbackException = null;
        await unit.DisposeAsync();
        Assert.False(context.IsOpen);
        Assert.Equal(1, factory.Postgres.Rollbacks);
    }

    private static async Task BeginAbandonedWriteAsync(IServiceScope scope)
    {
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync();
        await scope.ServiceProvider.GetRequiredService<IPostgresDataApiClient>().ExecuteAsync(
            "DELETE FROM expenses WHERE id = :id", [new("id", PostgresValue.FromLong(7))]);
    }

    private static async Task AssertDisposedAsync(CustomWebApplicationFactory factory, HttpClient client,
        PostgresTransactionContext context, string before, bool abandoned)
    {
        Assert.False(context.IsOpen);
        Assert.Equal(abandoned ? 1 : 0, factory.Postgres.Rollbacks);
        Assert.Equal(0, factory.Postgres.Commits);
        Assert.Equal(before, await client.GetStringAsync("/Expense/7"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Expense/latest/1")).StatusCode);
        if (abandoned)
        {
            using var next = factory.Services.CreateScope();
            await next.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync();
            next.Dispose();
            Assert.Equal(2, factory.Postgres.Rollbacks);
        }
    }

    private static CustomWebApplicationFactory CreateFactory()
    {
        var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        factory.Postgres.NextTransactionId = "scope-transaction";
        factory.Postgres.SeedExpense(7, "2026-07-02T13:23:45Z", "12.3400", "Groceries");
        return factory;
    }
}
