using GuitoApi.Configuration;
using Microsoft.Extensions.Options;

namespace GuitoApi.Middleware;

public sealed class HumanSessionBoundaryMiddleware(RequestDelegate next, IOptions<AppConfigurationOptions> options)
{
    public static bool IsSessionPath(PathString path)
    {
        var value = path.Value?.TrimEnd('/');
        return string.Equals(value, "/Auth/session/token", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "/Auth/session/refresh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "/Auth/session/logout", StringComparison.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsSessionPath(context.Request.Path)) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        if (HttpMethods.IsOptions(context.Request.Method)) { await next(context); return; }
        var origin = context.Request.Headers.Origin;
        var allowed = origin.Count == 1 && IsExplicitOrigin(origin[0]) && options.Value.Cors.AllowedOrigins.Contains(origin[0]!);
        if (!allowed || context.Request.Headers["X-Guito-Session"] != "1")
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        if (!context.Request.HasJsonContentType())
        { context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType; return; }
        await next(context);
    }

    private static bool IsExplicitOrigin(string? origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && uri.GetLeftPart(UriPartial.Authority) == origin;
}
