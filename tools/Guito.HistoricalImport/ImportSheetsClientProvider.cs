using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using GuitoApi.Infrastructure.Sheets;
namespace Guito.HistoricalImport;

internal sealed class ImportSheetsClientProvider(string keyFile) : IGooglesheetsClientProvider, IDisposable
{
    private readonly List<SheetsService> ownedClients = [];
    private bool disposed;

    public async Task<SheetsService> GetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var stream = File.OpenRead(keyFile);
        var credential = (await GoogleCredential.FromStreamAsync(stream, cancellationToken)).CreateScoped(SheetsService.Scope.SpreadsheetsReadonly);
        var client = new SheetsService(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "Guito historical snapshot read only" });
        ownedClients.Add(client);
        return client;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var client in ownedClients) client.Dispose();
        ownedClients.Clear();
    }
}
