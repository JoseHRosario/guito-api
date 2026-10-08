namespace GuitoApi.DataTransferObjects.Output;

/// <summary>
/// Outcome of finishing the bank auth flow (issue #89). Consent/session ids stay
/// EB-adapter internals (ADR-0004) — only the linking count surfaces to the UI.
/// </summary>
public class BankAuthResult
{
    public int AccountsLinked { get; set; }
}