using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
        var lifecycle = new SynchronousRequestScopeStartupFilter();
        using var host = WithLifecycle(factory, lifecycle);
        using var client = host.CreateClient();

        // Act
        using var response = await client.GetAsync(route);
        var failure = await lifecycle.DisposalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(failure);
        Assert.Equal(0, factory.Postgres.Commits);
        Assert.Equal(0, factory.Postgres.Rollbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_ShouldRollbackAbandonedCreationWithoutLeaks_WhenHostDisposesRequestScopeAsync(bool synchronous)
    {
        // Arrange
        using var factory = CreateFactory();
        var lifecycle = new SynchronousRequestScopeStartupFilter(synchronous);
        using var host = WithLifecycle(factory, lifecycle);
        using var client = host.CreateClient();
        using var observer = factory.CreateClient();
        var before = await observer.GetStringAsync("/Expense/history");
        using var request = AbandonedCreateRequest();

        // Act
        using var response = await client.SendAsync(request);
        var failure = await lifecycle.DisposalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(failure);
        await AssertRestoredAsync(factory, observer, before);
        using var next = AbandonedCreateRequest();
        using var repeated = await client.SendAsync(next);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(2, factory.Postgres.Rollbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_ShouldSurfaceRollbackFailureAndRetainRecoverableUnit_WhenProviderRejectsHostDisposalAsync(bool synchronous)
    {
        // Arrange
        using var factory = CreateFactory();
        var failure = new InvalidOperationException("Rollback transport failed.");
        factory.Postgres.RollbackException = failure;
        var lifecycle = new SynchronousRequestScopeStartupFilter(synchronous, () => factory.Postgres.RollbackException = null);
        using var host = WithLifecycle(factory, lifecycle);
        using var client = host.CreateClient();
        using var observer = factory.CreateClient();
        var before = await observer.GetStringAsync("/Expense/history");
        using var request = AbandonedCreateRequest();

        // Act
        using var response = await client.SendAsync(request);
        var actual = await lifecycle.DisposalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Same(failure, actual);
        await AssertRestoredAsync(factory, observer, before);
    }

    [Fact]
    public async Task Http_ShouldWaitForRollbackCompletion_WhenHostDisposesRequestSynchronouslyAsync()
    {
        // Arrange
        using var factory = CreateFactory();
        var lifecycle = new SynchronousRequestScopeStartupFilter();
        using var host = WithLifecycle(factory, lifecycle);
        using var client = host.CreateClient();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Postgres.RollbackCompletion = completion.Task;
        using var request = AbandonedCreateRequest();

        // Act
        var response = client.SendAsync(request);
        await factory.Postgres.RollbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completedBeforeRollback = lifecycle.DisposalCompleted.Task.IsCompleted;
        completion.SetResult();
        using var result = await response.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await lifecycle.DisposalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.False(completedBeforeRollback);
        Assert.Null(failure);
        Assert.Equal(1, factory.Postgres.Rollbacks);
        Assert.Equal(0, factory.Postgres.Commits);
    }

    private static WebApplicationFactory<Program> WithLifecycle(CustomWebApplicationFactory factory,
        SynchronousRequestScopeStartupFilter lifecycle) => factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(lifecycle)));

    private static HttpRequestMessage AbandonedCreateRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/Expense");
        request.Headers.Add("X-Test-Abandon-Transaction", "true");
        request.Content = JsonContent.Create(new { description = "Abandoned creation", category = "Groceries", amount = "12.3400", bookingDate = "2026-07-02" });
        return request;
    }

    private static async Task AssertRestoredAsync(CustomWebApplicationFactory factory, HttpClient observer, string before)
    {
        Assert.Equal(1, factory.Postgres.Rollbacks);
        Assert.Equal(0, factory.Postgres.Commits);
        Assert.Equal(before, await observer.GetStringAsync("/Expense/history"));
        Assert.Equal(HttpStatusCode.OK, (await observer.GetAsync("/Expense/7")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await observer.GetAsync("/Expense/latest/1")).StatusCode);
    }

    private static CustomWebApplicationFactory CreateFactory()
    {
        var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        factory.Postgres.NextTransactionId = "scope-transaction";
        factory.Postgres.SeedExpense(7, "2026-07-02T13:23:45Z", "12.3400", "Groceries");
        return factory;
    }
}
