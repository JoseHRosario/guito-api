using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using GuitoApi.Configuration;
using Microsoft.Extensions.Options;

namespace GuitoApi.Services
{
    public class GooglesheetsService : IGooglesheetsService
    {
        private readonly AppConfigurationOptions _options;

        public GooglesheetsService(IOptions<AppConfigurationOptions> options)
        {
            _options = options.Value;
        }

        public async Task<SheetsService> Get()
        {
            GoogleCredential credential;

            if (_options.Googlesheets.CredentialLocation == "Filesystem")
            {
                string credentialsFilePath = _options.Googlesheets.FilePath;
                using (var stream = new FileStream(credentialsFilePath, FileMode.Open, FileAccess.Read))
                {
                    credential = GoogleCredential.FromStream(stream)
                        .CreateScoped(SheetsService.Scope.Spreadsheets);
                }
            }
            else
            {
                throw new InvalidOperationException("Invalid credential location");
            }

            // Create Google Sheets API service.
            return new SheetsService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = "Guito API",
            });
        }
    }
}