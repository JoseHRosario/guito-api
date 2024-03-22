using GuitoApi.Configuration;
using GuitoApi.Middleware;
using GuitoApi.Services;
using Microsoft.Extensions.Configuration;
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
            services.Configure<AppConfigurationOptions>(Configuration.GetSection(AppConfigurationOptions.AppConfiguration));
            services.AddControllers();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            services.AddEndpointsApiExplorer();
            services.AddSwaggerGen();
            services.AddCors(o => o.AddPolicy("AllowAll", builder =>
            {
                builder.AllowAnyOrigin()
                       .AllowAnyMethod()
                       .AllowAnyHeader();
            })
            );
            services.AddHealthChecks();
            services.AddHttpContextAccessor();

            services.AddScoped<IExpenseService, ExpenseGoogleApisSheetsService>();
            services.AddScoped<ICategoryService, CategoryGoogleApisSheetsService>();
            services.AddScoped<IGooglesheetsService, GooglesheetsService>();
            services.AddScoped<IUserIdentityResolver, UserIdentityResolver>();
            
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILoggerFactory loggerFactory)
        {
            if (env.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }
            else
            {
                app.UseHttpsRedirection();
            }

            app.UseCors("AllowAll");

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
