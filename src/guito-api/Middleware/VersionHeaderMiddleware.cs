namespace GuitoApi.Middleware;

/// <summary>
/// Issue #75: stamps every response with the build version. The value is the
/// same stamp /healthz reports in its body (VersionInfo), so any request —
/// authenticated or not, success or failure — lets the UI (and humans in
/// browser devtools) see which build served it. Registered before the auth
/// middlewares so rejections carry it too. The header is also listed in the
/// CORS policies' exposed headers: without it, browsers hide it from JS.
/// </summary>
public class VersionHeaderMiddleware
{
    public const string HeaderName = "X-Api-Version";

    private readonly RequestDelegate _next;

    public VersionHeaderMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = VersionInfo.Version;
            return Task.CompletedTask;
        });
        return _next(context);
    }
}
