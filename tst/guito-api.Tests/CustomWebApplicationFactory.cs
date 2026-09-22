using GuitoApi.Services;
using GuitoApi.Services.Account;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>Shared stub for all Google Sheets traffic; tests assert against it.</summary>
    public FakeSheetsHttpHandler SheetsHandler { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            var sheets = SheetsHandler;
            Replace<IGooglesheetsService>(services, sp => new FakeGooglesheetsService(sheets));
            Replace<IListTransactionsService>(services, _ => new ListTransactionsDummyService());
        });
    }

    private static void Replace<TService>(IServiceCollection services, Func<IServiceProvider, TService> factory)
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(TService));
        services.Remove(descriptor);
        services.AddScoped(typeof(TService), _ => factory(_));
    }
}