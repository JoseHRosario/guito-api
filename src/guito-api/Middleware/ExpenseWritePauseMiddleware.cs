using GuitoApi.Configuration;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.Sheets;
using Microsoft.Extensions.Options;

namespace GuitoApi.Middleware;

public class ExpenseWritePauseMiddleware(RequestDelegate next, IOptions<AppConfigurationOptions> options)
{
    public async Task InvokeAsync(HttpContext context, ISheetScopeResolver scope)
    {
        // Run after both auth gates, before controller/repository resolution. Smoke is
        // deliberately isolated in Sheets and never participates in the personal pause.
        if (options.Value.ExpenseCutover.WritesPaused && HttpMethods.IsPost(context.Request.Method)
            && string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/Expense", StringComparison.OrdinalIgnoreCase)
            && !scope.IsSmokeScope)
            throw new ProblemException(StatusCodes.Status503ServiceUnavailable, "Personal Expense writes are paused for controlled cutover. Retry after reopening.");
        await next(context);
    }
}
