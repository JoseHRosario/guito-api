using System.Net;

namespace GuitoApi.Middleware
{
    /// <summary>
    /// Writes a plain-text auth error response — shared by ApiKeyMiddleware and
    /// GoogleIdTokenMiddleware (was byte-identical in both).
    /// Status-code convention: missing credentials → 401 Unauthorized;
    /// credentials present but rejected → 403 Forbidden.
    /// </summary>
    public static class AuthErrorResponseWriter
    {
        public static Task WriteAsync(HttpContext context, HttpStatusCode statusCode, string errorMessage)
        {
            context.Response.StatusCode = (int)statusCode;
            return context.Response.WriteAsync(errorMessage);
        }
    }
}