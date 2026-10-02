using GuitoApi.Configuration;
using GuitoApi.Infrastructure.Secrets;
using GuitoApi.Infrastructure.Sheets;
using GuitoApi.Exceptions;
using GuitoApi.Middleware;
using GuitoApi.Repositories;
using GuitoApi.Services;
using GuitoApi.Services.Account;
using GuitoApi.Services.ArtificialIntelligence;
using GuitoApi.Services.Category;
using GuitoApi.Services.Auth;
using GuitoApi.Services.Expense;
using Serilog;

namespace GuitoApi
{
    public class Startup
    {
        private IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration) => Configuration = configuration;

        public void ConfigureServices(IServiceCollection services)
        {
            var environment = Configuration.GetValue<string>("ASPNETCORE_ENVIRONMENT") ?? Environments.Production;

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
                           .AllowAnyHeader();
                });
                o.AddPolicy("AllowOnlyWebApp", builder =>
                {
                    // Browser origins from config (CORS section); the deployed UI's
                    // real origin (CloudFront) must be here or every browser call
                    // — token exchange included — is CORS-blocked.
                    builder.WithOrigins(allowedOrigins)
                           .AllowAnyMethod()
                           .AllowAnyHeader();
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

            services.AddScoped<ICreateExpenseService, CreateExpenseGoogleApisSheetsService>();
            services.AddScoped<IMatchExpensesService, MatchExpensesService>();
            services.AddScoped<IListLatestExpensesService, ListLatestExpensesGoogleApisSheetsService>();
            services.AddScoped<IDeleteExpenseRowService, DeleteExpenseGoogleApisSheetsService>();
            services.AddScoped<ISheetScopeResolver, SheetScopeResolver>();
            services.AddScoped<IListCategoryService, ListCategoryGoogleApisSheetsService>();
            services.AddScoped<IListTransactionsService, ListTransactionsNordigenService>();
            services.AddHttpClient(nameof(ListTransactionsNordigenService));
            services.AddScoped<IGooglesheetsClientProvider, GooglesheetsClientProvider>();
            if (environment == Environments.Development)
            {
                // Local dev: canned bank transactions so /expense/match works end-to-end
                // without PSD2 credentials. PSD2 wiring returns in phase 1.
                services.AddScoped<IListTransactionsService, ListTransactionsDummyService>();
            }
            services.AddScoped<IExtractMethodService, ExtractMethodService>();
            services.AddScoped<IUserIdentityResolver, UserIdentityResolver>();
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
            // Agent key path (ADR-0003) first: X-Api-Key gate; independent of the Google token path.
            app.UseMiddleware<ApiKeyMiddleware>();
            app.UseMiddleware<GoogleIdTokenMiddleware>();
            app.UseRouting();
            app.UseResponseCaching();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapHealthChecks("/healthz");
                endpoints.MapControllers();
            });
        }
    }
}
