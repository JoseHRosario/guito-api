using GuitoApi.Exceptions;

namespace GuitoApi.Middleware;

/// <summary>Prevents retired smoke traffic from becoming personal datastore operations.</summary>
public sealed class RetiredSheetScopeMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("X-Sheet-Scope"))
            throw new ProblemException(400, "X-Sheet-Scope is retired; remove the header.");
        return next(context);
    }
}
