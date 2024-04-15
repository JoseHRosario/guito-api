using Microsoft.AspNetCore.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace GuitoApi.Exceptions
{
    [SuppressMessage("Usage", "CS8602:Dereference of a possibly null reference")]
    public class ExceptionToProblemDetailsHandler : IExceptionHandler
    {
        private readonly IProblemDetailsService _problemDetailsService;

        public ExceptionToProblemDetailsHandler(IProblemDetailsService problemDetailsService)
        {
            _problemDetailsService = problemDetailsService;
        }
#pragma warning disable CS8602 
        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            int statusCode = 500;
            exception ??= new Exception("An error as occured");
            if (exception is ProblemException)
            {
                statusCode = (exception as ProblemException).HttpStatusCode;
            }
            httpContext.Response.StatusCode = statusCode;
            return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails =
                {
                    Title = "An error as occured",
                    Detail = exception?.Message,
                    Type = exception.GetType().Name,
                },
                Exception = exception
            });
        }
#pragma warning restore CS8602 
    }
}
