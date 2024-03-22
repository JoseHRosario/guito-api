using Serilog;

namespace GuitoApi
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseIISIntegration().UseStartup<Startup>();
                })
                .UseSerilog()
                .Build()
                .Run();
        }
    }
}
