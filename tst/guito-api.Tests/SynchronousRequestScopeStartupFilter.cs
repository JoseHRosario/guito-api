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
            abandonedUnit = await BeginAbandonedUnitAsync(context, scope.ServiceProvider);
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
            await DisposeModeAsync(scope);
        }
        catch (Exception exception)
        {
            failure = exception;
            if (recoverRollback is null || abandonedUnit is null) throw;
            await RecoverRollbackAsync(abandonedUnit);
        }
        finally
        {
            DisposalCompleted.TrySetResult(failure);
        }
    }

    private async Task DisposeModeAsync(AsyncServiceScope scope)
    {
        if (synchronous) scope.Dispose();
        else await scope.DisposeAsync();
    }

    private static async Task<IUnitOfWork?> BeginAbandonedUnitAsync(HttpContext context, IServiceProvider services)
    {
        // A host-abandoned transaction wraps the actual create action, not test SQL.
        if (!context.Request.Headers.ContainsKey("X-Test-Abandon-Transaction")) return null;
        var unit = services.GetRequiredService<IUnitOfWork>();
        await unit.BeginAsync(context.RequestAborted);
        return unit;
    }

    private async Task RecoverRollbackAsync(IUnitOfWork abandonedUnit)
    {
        // Retry the same unit after restoring transport; only an uncleared context can roll back.
        recoverRollback!();
        await abandonedUnit.DisposeAsync();
    }
}
