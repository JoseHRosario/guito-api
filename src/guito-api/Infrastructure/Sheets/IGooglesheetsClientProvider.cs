using Google.Apis.Sheets.v4;

namespace GuitoApi.Infrastructure.Sheets
{
    public interface IGooglesheetsClientProvider
    {
        public Task<SheetsService> GetAsync(CancellationToken cancellationToken = default);
    }
}
