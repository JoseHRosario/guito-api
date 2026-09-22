using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using GuitoApi.Configuration;
using GuitoApi.Exceptions;
using Microsoft.Extensions.Options;

namespace GuitoApi.Services
{
    public class GooglesheetsService : IGooglesheetsService
    {
        public const string CredentialLocationFilesystem = "Filesystem";

        private readonly AppConfigurationOptions _options;

        public GooglesheetsService(IOptions<AppConfigurationOptions> options)
        {
            _options = options.Value;
        }

        public async Task<SheetsService> Get()
        {
            GoogleCredential credential;

            if (_options.Googlesheets.CredentialLocation == CredentialLocationFilesystem)
            {
                string credentialsFilePath = _options.Googlesheets.FilePath;
                if (!File.Exists(credentialsFilePath))
                {
                    throw new ProblemException(
                        message: $"Google service-account key not found at '{Path.GetFullPath(credentialsFilePath)}'. " +
                                 "Place the key file (src/google-spreadsheets.json for local dev) or configure another credential location.");
                }

                await using (var stream = new FileStream(credentialsFilePath, FileMode.Open, FileAccess.Read))
                {
                    credential = GoogleCredential.FromStream(stream)
                        .CreateScoped(SheetsService.Scope.Spreadsheets);
                }
            }
            else
            {
                throw new ProblemException(
                    message: $"Unsupported Google credential location '{_options.Googlesheets.CredentialLocation}'. " +
                             $"Supported values: {CredentialLocationFilesystem}.");
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