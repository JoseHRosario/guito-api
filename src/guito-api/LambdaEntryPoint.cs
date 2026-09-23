using Microsoft.AspNetCore.Hosting;

namespace GuitoApi
{
    /// <summary>
    /// Lambda bootstrap for the API Gateway HTTP API payload (issue #3).
    /// Same Startup as local `dotnet run`; Kestrel is never started in Lambda.
    /// Inert locally — only Lambda instantiates this class.
    /// </summary>
    public class LambdaEntryPoint : Amazon.Lambda.AspNetCoreServer.APIGatewayHttpApiV2ProxyFunction
    {
        protected override void Init(IWebHostBuilder builder)
        {
            builder.UseStartup<Startup>();
        }
    }
}
