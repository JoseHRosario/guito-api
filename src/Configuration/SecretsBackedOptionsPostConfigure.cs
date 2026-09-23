using GuitoApi.Services;
using Microsoft.Extensions.Options;

namespace GuitoApi.Configuration
{
    /// <summary>
    /// When the API runs against Secrets (production), the production spreadsheet id
    /// comes from the secret payload, not appsettings — one place to rotate it.
    /// Local dev (Filesystem credentials) keeps ids from appsettings.
    /// Resolves ISecretsProvider from a fresh scope: options post-configure runs as a
    /// singleton while the provider may be registered scoped.
    /// </summary>
    public class SecretsBackedOptionsPostConfigure : IPostConfigureOptions<AppConfigurationOptions>
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public SecretsBackedOptionsPostConfigure(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public void PostConfigure(string? name, AppConfigurationOptions options)
        {
            if (options.Googlesheets.CredentialLocation != GooglesheetsService.CredentialLocationSecrets)
                return;

            using var scope = _scopeFactory.CreateScope();
            var secretsProvider = scope.ServiceProvider.GetRequiredService<ISecretsProvider>();
            var payload = secretsProvider.GetAsync().GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(payload.SpreadsheetId))
                options.Googlesheets.SpreadsheetId = payload.SpreadsheetId;
        }
    }
}
