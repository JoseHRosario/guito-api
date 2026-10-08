using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankConnections;

public interface IListBankConnectionsService
{
    Task<BankConnectionList> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Read view over the linked bank accounts for the Settings bank-connection card
/// (issue #116). The IBAN is masked here — only the last 4 digits leave the API.
/// </summary>
public class ListBankConnectionsService(IBankAccountRepository accounts) : IListBankConnectionsService
{
    public async Task<BankConnectionList> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await accounts.ListAsync(cancellationToken);
        return new BankConnectionList
        {
            Accounts = rows.Select(row => new BankConnection
            {
                Name = row.Name,
                IbanMasked = MaskIban(row.Iban),
                Currency = row.Currency,
                AspspName = row.AspspName,
                AspspCountry = row.AspspCountry,
                ConsentStatus = row.ConsentStatus,
                ConsentExpiresAt = row.ConsentExpiresAt,
            }).ToList(),
        };
    }

    private static string MaskIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban) || iban.Length < 4) return string.Empty;
        return "**** " + iban[^4..];
    }
}