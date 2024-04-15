using GuitoApi.Configuration;
using GuitoApi.Exceptions;
using GuitoApi.Middleware;
using GuitoApi.Services;
using GuitoApi.Services.Account;
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

            }
            );
            services.AddHealthChecks();
            services.AddHttpContextAccessor();
            services.AddExceptionHandler<ExceptionToProblemDetailsHandler>();

            services.Configure<AppConfigurationOptions>(Configuration.GetSection(AppConfigurationOptions.AppConfiguration));
            services.AddScoped<ICreateExpenseService, CreateExpenseGoogleApisSheetsService>();
            services.AddScoped<IMatchExpensesService, MatchExpensesService>();
            services.AddScoped<IListLatestExpensesService, ListLatestExpensesGoogleApisSheetsService>();
            services.AddScoped<IListCategoryService, ListCategoryGoogleApisSheetsService>();
            services.AddScoped<IListTransactionsService, ListTransactionsNordigenService>();
            //services.AddScoped<IListTransactionsService, ListTransactionsDummyService>();
            services.AddScoped<IGooglesheetsService, GooglesheetsService>();
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
            app.UseMiddleware<GoogleIdTokenMiddleware>();
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapHealthChecks("/healthz");

                endpoints.MapControllers();
            });
        }
    }
}
