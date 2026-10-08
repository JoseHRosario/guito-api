namespace GuitoApi.Exceptions
{
    /// <summary>
    /// The bank consent needs to be re-established (issue #89, ADR-0004): EB session
    /// expired/unknown, or no bank account is linked yet. Mapped to HTTP 409 — the UI
    /// reacts by starting the auth flow again (GET /BankAuth/url).
    /// </summary>
    public class BankReconnectionRequiredException : ProblemException
    {
        public BankReconnectionRequiredException(string? message = null) : base(409, message)
        {
        }
    }
}