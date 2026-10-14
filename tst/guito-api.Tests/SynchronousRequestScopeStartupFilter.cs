using GuitoApi.Infrastructure.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

/// <summary>Replays host request-scope disposal around the real HTTP pipeline.</summary>
internal sealed class SynchronousRequestScopeStartupFilter(bool synchronous = true, Action? recoverRollback = null) : IStartupFilter
{
    public TaskCompletionSource<Exception?> DisposalCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(InvokeAsync);
        next(app);
    };

    private async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var original = context.RequestServices;
        var scope = original.CreateAsyncScope();
        context.RequestServices = scope.ServiceProvider;
        IUnitOfWork? abandonedUnit = null;
        try
        {
            // A host-abandoned transaction wraps the actual create action, not test SQL.
            if (context.Request.Headers.ContainsKey("X-Test-Abandon-Transaction"))
            {
                abandonedUnit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await abandonedUnit.BeginAsync(context.RequestAborted);
            }
            await next(context);
        }
        finally
        {
            context.RequestServices = original;
            await DisposeScopeAsync(scope, abandonedUnit);
        }
    }

    private async Task DisposeScopeAsync(AsyncServiceScope scope, IUnitOfWork? abandonedUnit)
    {
        Exception? failure = null;
        try
        {
            if (synchronous) scope.Dispose();
            else await scope.DisposeAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
            if (recoverRollback is null || abandonedUnit is null) throw;
            // Retry the same unit after restoring transport; only an uncleared context can roll back.
            recoverRollback();
            await abandonedUnit.DisposeAsync();
        }
        finally
        {
            DisposalCompleted.TrySetResult(failure);
        }
    }
}
