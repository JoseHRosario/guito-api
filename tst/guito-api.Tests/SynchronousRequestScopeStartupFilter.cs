using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

/// <summary>Replays Lambda's synchronous request-scope disposal after the real HTTP pipeline.</summary>
internal sealed class SynchronousRequestScopeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(InvokeAsync);
        next(app);
    };

    private static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var original = context.RequestServices;
        using var scope = original.CreateScope();
        context.RequestServices = scope.ServiceProvider;
        try
        {
            await next(context);
        }
        finally
        {
            context.RequestServices = original;
        }
    }
}
