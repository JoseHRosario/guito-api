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
            return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails =
                {
                    Title = "An error has occurred",
                    Detail = exception.Message,
                    Type = exception.GetType().Name,
                },
                Exception = exception
            });
        }
    }
}