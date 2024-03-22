using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using GuitoApi.Configuration;
using Microsoft.Extensions.Options;
using System.Text;

namespace GuitoApi.Services
{
    public class GooglesheetsService : IGooglesheetsService
    {
        private readonly IConfiguration _configuration;
        private readonly AppConfigurationOptions _options;

        public GooglesheetsService(IConfiguration configuration, IOptions<AppConfigurationOptions> options)
        {
            _configuration = configuration;
            _options = options.Value;
        }

        public async Task<SheetsService> Get()
        {
            GoogleCredential credential;

            if (_options.Googlesheets.CredentialLocation == "AzureBlobStorage")
            {
                string connectionString = _configuration.GetConnectionString("AzureBlobStorage") ??
                throw new ArgumentNullException("AzureBlobStorage connections string is empty or non existent");

                BlobServiceClient blobServiceClient = new BlobServiceClient(connectionString);
                BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(_options.Googlesheets.ContainerName);
                BlobClient blobClient = containerClient.GetBlobClient(_options.Googlesheets.BlobName);
                BlobDownloadInfo download = await blobClient.DownloadAsync();
                using (StreamReader reader = new StreamReader(download.Content, Encoding.UTF8))
                {
                    string content = await reader.ReadToEndAsync();
                    credential = GoogleCredential.FromJson(content)
                        .CreateScoped(SheetsService.Scope.Spreadsheets);
                }
            }
            else if (_options.Googlesheets.CredentialLocation == "Filesystem")
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
                ApplicationName = "Google Sheets API Example",
            });

        }
    }
}
