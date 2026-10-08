using Amazon.RDSDataService;
using GuitoApi.Configuration;
using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Infrastructure.Secrets;
using GuitoApi.Infrastructure.Sheets;
using GuitoApi.Infrastructure.AI;
using GuitoApi.Infrastructure.EnableBanking;
using GuitoApi.Services.BankAuth;
using GuitoApi.Exceptions;
using GuitoApi.Middleware;
using GuitoApi.Repositories;
using GuitoApi.Services;
using GuitoApi.Services.Account;
using GuitoApi.Services.ArtificialIntelligence;
using GuitoApi.Services.Category;
using GuitoApi.Services.Auth;
using GuitoApi.Services.Expense;
using Microsoft.Extensions.Options;
using Serilog;

namespace GuitoApi
{
    public class Startup
    {
        private IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration) => Configuration = configuration;

        public void ConfigureServices(IServiceCollection services)
        {
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(Configuration)
                .CreateLogger();

            services.AddControllers();
            services.AddProblemDetails();
            services.AddEndpointsApiExplorer();
            services.AddSwaggerGen();
            services.AddCors(o =>
            {
                var allowedOrigins = Configuration.GetSection("AppConfiguration:Cors:AllowedOrigins").Get<string[]>() ?? [];
                o.AddPolicy("AllowAll", builder =>
                {
                    builder.AllowAnyOrigin()
                           .AllowAnyMethod()
                           .AllowAnyHeader()
                           .WithExposedHeaders(VersionHeaderMiddleware.HeaderName);
                });
                o.AddPolicy("AllowOnlyWebApp", builder =>
                {
                    // Browser origins from config (CORS section); the deployed UI's
                    // real origin (CloudFront) must be here or every browser call
                    // — token exchange included — is CORS-blocked.
                    builder.WithOrigins(allowedOrigins)
                           .AllowAnyMethod()
                           .AllowAnyHeader()
                           .WithExposedHeaders(VersionHeaderMiddleware.HeaderName);
                });
            });
            services.AddHealthChecks();
            services.AddHttpContextAccessor();
            services.AddExceptionHandler<ExceptionToProblemDetailsHandler>();
            services.AddResponseCaching();

            services.Configure<AppConfigurationOptions>(Configuration.GetSection(AppConfigurationOptions.AppConfiguration));

            // Runtime secrets (issue #3): Secrets Manager in production, gitignored local file in dev.
            var secretsLocation = Configuration.GetValue<string>("AppConfiguration:Secrets:Location");
            services.AddSingleton<ISecretsProvider>(CreateSecretsProvider(secretsLocation));

            // Google human-auth client secret (issue #52): same location pattern as the runtime secrets.
            services.AddSingleton<IHumanAuthSecretProvider>(CreateHumanAuthSecretProvider(secretsLocation));
            services.AddScoped<ITokenExchangeService, GoogleTokenExchangeService>();
            services.AddHttpClient(GoogleTokenExchangeService.HttpClientName);

            // Google token revocation on sign-out (issue #64): the session's access token → Google's revoke endpoint.
            services.AddScoped<IRevokeGoogleTokenService, RevokeGoogleTokenService>();
            services.AddHttpClient(RevokeGoogleTokenService.HttpClientName);

            services.AddScoped<IExpenseRepository, GoogleSheetsExpenseRepository>();
            services.AddScoped<ICategoryRepository, GoogleSheetsCategoryRepository>();

            services.AddScoped<ICreateExpenseService, CreateExpenseService>();
            services.AddScoped<IMatchExpensesService, MatchExpensesService>();
            services.AddScoped<IListLatestExpensesService, ListLatestExpensesService>();
            services.AddScoped<IDeleteExpenseRowService, DeleteExpenseService>();
            services.AddScoped<ISheetScopeResolver, SheetScopeResolver>();
            services.AddScoped<IListCategoryService, ListCategoryGoogleApisSheetsService>();
            // Bank transaction provider selected by configuration (ADR-0004): EnableBanking is
            // the PSD2 provider (issue #89); Dummy serves canned data for local dev and CI.
            services.AddScoped<IListTransactionsService>(sp =>
            {
                var bankProvider = sp.GetRequiredService<IOptions<AppConfigurationOptions>>().Value.BankProvider;
                return bankProvider switch
                {
                    "Dummy" => new ListTransactionsDummyService(),
                    "EnableBanking" => new ListTransactionsEnableBankingService(
                        sp.GetRequiredService<IEnableBankingClient>(),
                        sp.GetRequiredService<IBankAccountRepository>()),
                    _ => throw new InvalidOperationException($"Unknown BankProvider '{bankProvider}'.")
                };
            });
            // Enable Banking adapter internals (issue #89, ADR-0004): fresh JWT client
            // assertion per request, transport seam faked in tests, typed client on top.
            services.Configure<EnableBankingOptions>(Configuration.GetSection(AppConfigurationOptions.AppConfiguration).GetSection("EnableBanking"));
            services.AddScoped<IEnableBankingJwtSigner, EnableBankingJwtSigner>();
            services.AddHttpClient(HttpEnableBankingTransport.HttpClientName, (sp, client) =>
                client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<EnableBankingOptions>>().Value.ApiBaseUrl));
            services.AddScoped<IEnableBankingTransport, HttpEnableBankingTransport>();
            services.AddScoped<IEnableBankingClient, EnableBankingClient>();
            services.AddScoped<IBankAccountRepository, DataApiPostgresBankAccountRepository>();
            services.AddScoped<IGetBankAuthUrlService, GetBankAuthUrlService>();
            services.AddScoped<IFinishBankAuthService, FinishBankAuthService>();
            services.AddScoped<IGooglesheetsClientProvider, GooglesheetsClientProvider>();
            services.AddScoped<IExtractMethodService, ExtractMethodService>();
            services.AddScoped<IExpenseExtractionRepository, OpenRouterExpenseExtractionRepository>();
            services.AddScoped<IOpenRouterClientProvider, OpenRouterClientProvider>();
            services.AddHttpClient(OpenRouterClientProvider.HttpClientName);
            services.AddScoped<IUserIdentityResolver, UserIdentityResolver>();

            // Data API (issue #87 + unit of work): the fake seam for all Postgres access.
            services.Configure<DatabaseOptions>(Configuration.GetSection(DatabaseOptions.SectionName));
            services.AddSingleton<IAmazonRDSDataService, AmazonRDSDataServiceClient>();
            services.AddScoped<PostgresTransactionContext>();
            services.AddScoped<IPostgresDataApiClient, DataApiClient>();
            services.AddScoped<IUnitOfWork, DataApiUnitOfWork>();
            // Bank Transactions aggregate (issue #88): ADR-0011 shape — interface in
            // Repositories/, Data API implementation here in Infrastructure/Postgres/.
            services.AddScoped<IBankTransactionRepository, DataApiPostgresBankTransactionRepository>();
        }

        private ISecretsProvider CreateSecretsProvider(string? secretsLocation) => secretsLocation switch
        {
            SecretsConfig.LocationAws => new AwsSecretsProvider(RequiredAwsSecretName()),
            // Anything other than "Aws" is a local dev checkout: file-backed secrets.
            _ => new FileSecretsProvider(Configuration.GetValue<string>("AppConfiguration:Secrets:FilePath") ?? "secrets.local.json"),
        };

        /// <summary>The Secrets config section bound to SecretsConfig — defaults live there, not here.</summary>
        private SecretsConfig Secrets => Configuration.GetSection("AppConfiguration:Secrets").Get<SecretsConfig>() ?? new SecretsConfig();

        private IHumanAuthSecretProvider CreateHumanAuthSecretProvider(string? secretsLocation) => secretsLocation switch
        {
            SecretsConfig.LocationAws => new AwsHumanAuthSecretProvider(Secrets.HumanAuthSecretName),
            _ => new FileHumanAuthSecretProvider(Secrets.HumanAuthFilePath),
        };

        private string RequiredAwsSecretName() =>
            Secrets.SecretName
            ?? throw new InvalidOperationException("AppConfiguration:Secrets:SecretName is required when Secrets:Location is Aws");

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILoggerFactory loggerFactory)
        {
            if (env.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
                app.UseCors("AllowAll");
            }
            else
            {
                app.UseHttpsRedirection();
                app.UseCors("AllowOnlyWebApp");
            }
            app.UseExceptionHandler();
            // Build-version stamp on every response (issue #75) — before the auth
            // middlewares so rejected requests carry it too.
            app.UseMiddleware<VersionHeaderMiddleware>();
            // Agent key path (ADR-0003) first: X-Api-Key gate; independent of the Google token path.
            app.UseMiddleware<ApiKeyMiddleware>();
            app.UseMiddleware<GoogleIdTokenMiddleware>();
            app.UseRouting();
            app.UseResponseCaching();
            app.UseEndpoints(endpoints =>
            {
                // /healthz stays a plain health probe (original behavior); the
                // build version rides the X-Api-Version response header
                // (VersionHeaderMiddleware), not this body (issue #75).
                endpoints.MapHealthChecks("/healthz");
                endpoints.MapControllers();
            });
        }
    }
}
