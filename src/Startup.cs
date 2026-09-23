using GuitoApi.Configuration;
using GuitoApi.Exceptions;
using GuitoApi.Middleware;
using GuitoApi.Services;
using GuitoApi.Services.Account;
using GuitoApi.Services.ArtificialIntelligence;
using GuitoApi.Services.Category;
using GuitoApi.Services.Expense;
using Serilog;

namespace GuitoApi
{
    public class Startup
    {
        private IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            var environment = Configuration.GetValue<string>("ASPNETCORE_ENVIRONMENT") ?? Environments.Production;

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(Configuration)
                .CreateLogger();

            // Add services to the container.
            services.AddControllers();
            services.AddProblemDetails();
            services.AddEndpointsApiExplorer();
            services.AddSwaggerGen();
            services.AddCors(o =>
            {
                o.AddPolicy("AllowAll", builder =>
                {
                    builder.AllowAnyOrigin()
                           .AllowAnyMethod()
                           .AllowAnyHeader();
                });
                o.AddPolicy("AllowOnlyWebApp", builder =>
                {
                    builder.WithOrigins("https://guito-web-app.vercel.app")
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
            if (secretsLocation == "Aws")
            {
                var secretName = Configuration.GetValue<string>("AppConfiguration:Secrets:SecretName")
                    ?? throw new InvalidOperationException("AppConfiguration:Secrets:SecretName is required when Secrets:Location is Aws");
                services.AddSingleton<ISecretsProvider>(new AwsSecretsProvider(secretName));
            }
            else
            {
                var secretsFilePath = Configuration.GetValue<string>("AppConfiguration:Secrets:FilePath") ?? "secrets.local.json";
                services.AddSingleton<ISecretsProvider>(new FileSecretsProvider(secretsFilePath));
            }

            services.AddScoped<ICreateExpenseService, CreateExpenseGoogleApisSheetsService>();
            services.AddScoped<IMatchExpensesService, MatchExpensesService>();
            services.AddScoped<IListLatestExpensesService, ListLatestExpensesGoogleApisSheetsService>();
            services.AddScoped<IListCategoryService, ListCategoryGoogleApisSheetsService>();
            services.AddScoped<IListTransactionsService, ListTransactionsNordigenService>();
            services.AddHttpClient(nameof(ListTransactionsNordigenService));
            services.AddScoped<IGooglesheetsService, GooglesheetsService>();
            if (environment == Environments.Development)
            {
                // Local dev: canned bank transactions so /expense/match works end-to-end
                // without PSD2 credentials. PSD2 wiring returns in phase 1.
                services.AddScoped<IListTransactionsService, ListTransactionsDummyService>();
            }
            services.AddScoped<IExtractMethodService, ExtractMethodService>();
            services.AddScoped<IUserIdentityResolver, UserIdentityResolver>();
        }

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
