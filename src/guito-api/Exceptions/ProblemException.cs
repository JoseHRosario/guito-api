using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Exceptions
{
    public class ProblemException : Exception
    {
        private const int DefaultHttpStatusCode = 500;
        public int HttpStatusCode { get; set; }

        public ProblemException(int? httpStatusCode = DefaultHttpStatusCode, string? message = null) : base(message)
        {
            HttpStatusCode = httpStatusCode ?? DefaultHttpStatusCode;
        }
    }
}
