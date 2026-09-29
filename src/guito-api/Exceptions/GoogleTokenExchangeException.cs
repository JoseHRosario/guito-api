namespace GuitoApi.Exceptions
{
    /// <summary>
    /// Google rejected the token exchange (issue #52): carries the RFC 6749
    /// error/error_description verbatim so the controller can surface them as
    /// the JSON body the UI already parses (guito-ui tokenFailureMessage).
    /// </summary>
    public class GoogleTokenExchangeException : Exception
    {
        public int HttpStatusCode { get; }
        public string? Error { get; }
        public string? ErrorDescription { get; }

        public GoogleTokenExchangeException(int httpStatusCode, string? error, string? errorDescription)
            : base(string.IsNullOrEmpty(errorDescription)
                ? $"Google token exchange failed: {error ?? "unknown error"}"
                : $"Google token exchange failed: {error}: {errorDescription}")
        {
            HttpStatusCode = httpStatusCode;
            Error = error;
            ErrorDescription = errorDescription;
        }
    }
}
