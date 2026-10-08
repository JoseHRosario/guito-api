namespace GuitoApi.Services.BankAuth
{
    /// <summary>Starts the UI-driven consent flow (issue #89, ADR-0004): EB POST /auth.</summary>
    public interface IGetBankAuthUrlService
    {
        Task<DataTransferObjects.Output.BankAuthUrl> GetAsync(string aspspName, string aspspCountry,
            CancellationToken cancellationToken = default);
    }
}