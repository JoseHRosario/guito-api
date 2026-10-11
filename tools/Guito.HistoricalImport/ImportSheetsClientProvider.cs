using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using GuitoApi.Infrastructure.Sheets;
namespace Guito.HistoricalImport;

internal sealed class ImportSheetsClientProvider(string keyFile) : IGooglesheetsClientProvider
{
    public async Task<SheetsService> GetAsync(CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(keyFile);
        var credential = (await GoogleCredential.FromStreamAsync(stream, cancellationToken)).CreateScoped(SheetsService.Scope.SpreadsheetsReadonly);
        return new SheetsService(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "Guito historical snapshot (read only)" });
    }
}
