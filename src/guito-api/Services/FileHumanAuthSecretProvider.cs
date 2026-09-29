using System.Text.Json;
using GuitoApi.Configuration;
using GuitoApi.Exceptions;

namespace GuitoApi.Services
{
    /// <summary>
    /// Human-auth client secret from a gitignored local JSON file (same schema as
    /// HumanAuthSecret); local-dev implementation. Path resolves against the
    /// project directory.
    /// </summary>
    public class FileHumanAuthSecretProvider : IHumanAuthSecretProvider
    {
        private readonly string _filePath;

        public FileHumanAuthSecretProvider(string filePath) => _filePath = filePath;

        public async Task<HumanAuthSecret> GetAsync(CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_filePath))
            {
                throw new ProblemException(
                    message: $"Human-auth secret file not found at '{Path.GetFullPath(_filePath)}'. " +
                             "Create it (human-auth.local.json in the project directory) or configure another secrets location.");
            }

            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            return JsonSerializer.Deserialize<HumanAuthSecret>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ProblemException(500, "Human-auth secret payload is invalid");
        }
    }
}
