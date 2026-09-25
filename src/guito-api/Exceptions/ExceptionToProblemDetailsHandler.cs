using Microsoft.AspNetCore.Diagnostics;

namespace GuitoApi.Exceptions
{
    public class ExceptionToProblemDetailsHandler : IExceptionHandler
    {
        private readonly IProblemDetailsService _problemDetailsService;
        private readonly ILogger<ExceptionToProblemDetailsHandler> _logger;

        public ExceptionToProblemDetailsHandler(
            IProblemDetailsService problemDetailsService,
            ILogger<ExceptionToProblemDetailsHandler> logger)
        {
            _problemDetailsService = problemDetailsService;
            _logger = logger;
        }

        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            var statusCode = exception is ProblemException problemException
                ? problemException.HttpStatusCode
                : 500;

            var isUnexpectedError = statusCode == 500 && exception is not ProblemException;
            if (isUnexpectedError)
            {
                // Server-side log keeps the detail; the client response stays masked.
                // The full details go into the MESSAGE itself, not just the exception
                // parameter: AWS Lambda's structured log formatter emits only the
                // rendered message, so a bare exception param is lost in CloudWatch
                // (seen live on T8.3 — a prod 500 was undiagnosable from logs alone).
                // Server-side only; the client response stays masked. NOTE: this
                // relies on exception details not carrying secrets — if an exception
                // source ever wraps bearer tokens/credentials, sanitize at the source.
                _logger.LogError(exception, "Unhandled exception for {Method} {Path} (trace {TraceId}): {ExceptionDetails}",
                    httpContext.Request.Method, httpContext.Request.Path,
                    httpContext.TraceIdentifier, exception.ToString());
            }

            httpContext.Response.StatusCode = statusCode;
            return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails =
                {
                    Title = "An error has occurred",
                    Detail = isUnexpectedError
                        ? "An unexpected error occurred. See server logs for details."
                        : exception.Message,
                    Type = isUnexpectedError ? "InternalError" : exception.GetType().Name,
                },
                Exception = exception
            });
        }
    }
}