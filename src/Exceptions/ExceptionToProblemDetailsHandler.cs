using Microsoft.AspNetCore.Diagnostics;

namespace GuitoApi.Exceptions
{
    public class ExceptionToProblemDetailsHandler : IExceptionHandler
    {
        private readonly IProblemDetailsService _problemDetailsService;

        public ExceptionToProblemDetailsHandler(IProblemDetailsService problemDetailsService)
        {
            _problemDetailsService = problemDetailsService;
        }

        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            var statusCode = exception is ProblemException problemException
                ? problemException.HttpStatusCode
                : 500;

            httpContext.Response.StatusCode = statusCode;
            var isUnexpectedError = statusCode == 500 && exception is not ProblemException;
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