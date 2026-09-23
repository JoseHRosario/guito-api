using System.Text.Json;
using GuitoApi.Configuration;
using GuitoApi.Exceptions;

namespace GuitoApi.Services
{
    /// <summary>
    /// Secrets from a gitignored local JSON file (same schema as SecretsPayload);
    /// local-dev implementation. Path resolves against the project directory.
    /// </summary>
    public class FileSecretsProvider : ISecretsProvider
    {
        private readonly string _filePath;

        public FileSecretsProvider(string filePath)
        {
            _filePath = filePath;
        }

        public async Task<SecretsPayload> GetAsync()
        {
            if (!File.Exists(_filePath))
            {
                throw new ProblemException(
                    message: $"Secrets file not found at '{Path.GetFullPath(_filePath)}'. " +
                             "Create it (src/secrets.local.json for local dev) or configure another secrets location.");
            }

            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<SecretsPayload>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ProblemException(500, "Secrets payload is invalid");
        }
    }
}
