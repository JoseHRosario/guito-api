
using GuitoApi.Configuration;
using GuitoApi.Middleware;
using GuitoApi.Services;

namespace GuitoApi
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.Configure<AppConfigurationOptions>(
                builder.Configuration.GetSection(AppConfigurationOptions.AppConfiguration));
            builder.Services.AddControllers();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddCors(o => o.AddPolicy("AllowAll", builder =>
                {
                    builder.AllowAnyOrigin()
                           .AllowAnyMethod()
                           .AllowAnyHeader();
                }));

            builder.Services.AddScoped<IExpenseService, ExpenseGoogleApisSheetsService>();
            builder.Services.AddScoped<ICategoryService, CategoryGoogleApisSheetsService>();
            builder.Services.AddScoped<IGooglesheetsService, GooglesheetsService>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
                app.UseCors("AllowAll");
            }

            app.UseHttpsRedirection();

            app.UseMiddleware<GoogleIdTokenMiddleware>();

            app.MapControllers();

            app.Run();
        }
    }
}
