namespace GuitoApi.Services.BankAuth
{
    /// <summary>Finishes the consent flow (issue #89): EB POST /sessions + account upsert.</summary>
    public interface IFinishBankAuthService
    {
        Task<DataTransferObjects.Output.BankAuthResult> FinishAsync(string code,
            CancellationToken cancellationToken = default);
    }
}