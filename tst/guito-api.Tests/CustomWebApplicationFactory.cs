using GuitoApi.Infrastructure.AI;
using GuitoApi.Infrastructure.EnableBanking;
using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Infrastructure.Secrets;
using GuitoApi.Infrastructure.Sheets;
using GuitoApi.Repositories;
using GuitoApi.Services.Account;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>Shared stub for all Google Sheets traffic; tests assert against it.</summary>
    public FakeSheetsHttpHandler SheetsHandler { get; } = new();

    /// <summary>Shared stub for all OpenRouter traffic; tests assert against it.</summary>
    public FakeOpenRouterHttpHandler OpenRouterHandler { get; } = new();

    /// <summary>Shared stub for all Enable Banking traffic (issue #89); tests assert against it.</summary>
    public FakeEnableBankingTransport EnableBankingTransport { get; } = new();

    /// <summary>Shared fake of the linked-accounts store (issue #89).</summary>
    public FakeBankAccountRepository BankAccounts { get; } = new();

    /// <summary>Shared fake of the bank-transactions store (issue #90).</summary>
    public FakeBankTransactionRepository BankTransactions { get; } = new();

    /// <summary>Shared fake of the Postgres categories mirror (issue #112).</summary>
    public FakeCategoriesRepository Categories { get; } = new();

    /// <summary>
    /// Shared fake of the RDS Data API seam (issue #87): hermetic suites must
    /// never construct the real DataApiClient (its AWS SDK client validates the
    /// ambient AWS config — a CI runner without it throws on every resolution).
    /// </summary>
    public FakePostgresDataApiClient Postgres { get; } = new();

    /// <summary>
    /// When true the bank provider stays EnableBanking (real adapter over the faked EB
    /// transport); the default keeps the Dummy provider, mirroring the default config.
    /// </summary>
    public bool UseEnableBankingProvider { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        if (UseEnableBankingProvider)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AppConfiguration:BankProvider"] = "EnableBanking",
                    ["AppConfiguration:EnableBanking:AuthCallbackUrl"] = "https://test/BankAuth/callback",
                }));
        }

        builder.ConfigureTestServices(services =>
        {
            var sheets = SheetsHandler;
            var openRouter = OpenRouterHandler;
            Replace<IGooglesheetsClientProvider>(services, sp => new FakeGooglesheetsClientProvider(sheets));
            Replace<IOpenRouterClientProvider>(services, _ => new FakeOpenRouterClientProvider(openRouter));
            var postgres = Postgres;
            Replace<IPostgresDataApiClient>(services, _ => postgres);
            if (!UseEnableBankingProvider)
                Replace<IListTransactionsService>(services, _ => new ListTransactionsDummyService());
            // Canned secrets: the repo has no real ones (gitignored by design).
            var descriptor = services.Single(d => d.ServiceType == typeof(ISecretsProvider));
            services.Remove(descriptor);
            services.AddSingleton<ISecretsProvider>(new FakeSecretsProvider());
            if (UseEnableBankingProvider)
            {
                Replace<IEnableBankingTransport>(services, _ => EnableBankingTransport);
                Replace<IBankAccountRepository>(services, _ => BankAccounts);
                Replace<IBankTransactionRepository>(services, _ => BankTransactions);
                Replace<ICategoriesRepository>(services, _ => Categories);
            }
        });
    }

    private void Replace<TService>(IServiceCollection services, Func<IServiceProvider, TService> factory)
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(TService));
        services.Remove(descriptor);
        services.AddScoped(typeof(TService), _ => factory(_)!);
    }
}