using System.Net;
using System.Text.Json;
using Amazon.RDSDataService;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.RDSDataService.Model;
using GuitoApi.Configuration;
using GuitoApi.Infrastructure.Auth;
using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Infrastructure.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace GuitoApi.Tests;

internal sealed class UserSessionTestHost(UserSessionTestHost.State state) : CustomWebApplicationFactory
{
    public const string Origin = "https://guito.test";
    public const string ClientId = "human-client";
    public const string Email = "human@example.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        UiOrigin = Origin;
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(logging =>
                logging.SetMinimumLevel(LogLevel.Trace).AddProvider(new SessionLogs(state))));
            services.PostConfigure<AppConfigurationOptions>(options =>
            {
                options.Authentication.ValidateApiKey = true;
                options.Authentication.ValidateIdToken = true;
                options.Authentication.GoogleClientId = ClientId;
                options.Authentication.OAuthAudience = state.InvalidGoogleConfiguration ? "wrong-client" : ClientId;
                options.Authentication.AllowedLogins = [Email];
                options.Cors.AllowedOrigins = [Origin];
                options.UserSessions.TableName = state.TableName;
                options.UserSessions.Region = state.Region;
            });
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(state.Clock);
            services.RemoveAll<ISecretsProvider>();
            services.AddSingleton<ISecretsProvider>(new KeySecrets(state));
            services.RemoveAll<IHumanAuthSecretProvider>();
            services.AddSingleton<IHumanAuthSecretProvider>(new FakeHumanAuthSecretProvider());
            services.RemoveAll<IPostgresDataApiClient>();
            services.RemoveAll<IAmazonDynamoDB>();
            services.AddSingleton<IAmazonDynamoDB>(state.Database);
            services.AddSingleton<SessionSdk>(_ => new SessionSdk(state));
            services.AddScoped<IPostgresDataApiClient>(provider => new DataApiClient(provider.GetRequiredService<SessionSdk>(), Options.Create(new DatabaseOptions()), new PostgresTransactionContext(),
                    clock: state.Clock, logger: provider.GetRequiredService<ILogger<DataApiClient>>()));
            services.AddHttpClient(GoogleSessionProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new TokenTransport(state));
            services.AddHttpClient(GuitoApi.Services.Auth.RevokeGoogleTokenService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new RejectLegacyGoogleTransport(state));
            services.AddHttpClient(GuitoApi.Services.Auth.GoogleTokenExchangeService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new RejectLegacyGoogleTransport(state));
            services.AddHttpClient("SheetsReportingJwks").ConfigurePrimaryHttpMessageHandler(() => state.Keys);
            services.AddHttpClient("UserSessionJwks").ConfigurePrimaryHttpMessageHandler(() => new KeyTransport(state));
        });
    }

    internal sealed class State
    {
        public State() => Database = new(this);
        public SessionDynamoDbClient Database { get; }
        public List<AmazonWebServiceRequest> DynamoRequests { get; } = [];
        public Func<AmazonWebServiceRequest, CancellationToken, Task>? BeforeDynamoRequest { get; set; }
        public Func<AmazonWebServiceRequest, CancellationToken, Task>? AfterDynamoRequest { get; set; }
        public string TableName { get; set; } = "hermetic-user-sessions";
        public string Region { get; set; } = "eu-west-1";
        public SessionTestClock Clock { get; } = new();
        public bool InvalidGoogleConfiguration { get; set; }
        public List<string> Statements { get; } = [];
        public ConcurrentQueue<string> Logs { get; } = new();
        public ConcurrentQueue<Exception> LoggedExceptions { get; } = new();
        public ReportingTokenFixture Keys { get; } = new();
        public string Key { get; set; } = Convert.ToBase64String(Enumerable.Range(1, 32).Select(number => (byte)number).ToArray());
        public string? KeyPayload { get; set; }
        public List<Dictionary<string, string?>> Forms { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? Body { get; set; }
        public string? RefreshCredential { get; set; } = "fake-refresh-credential";
        public Action<Dictionary<string, object>>? Claims { get; set; }
        public bool WrongSignature { get; set; }
        public int UnexpectedGoogleRequests { get; set; }
        public bool TimeoutFailure { get; set; }
        public bool KeyFailure { get; set; }
        public bool NetworkFailure { get; set; }
        public TaskCompletionSource? Entered { get; set; }
        public TaskCompletionSource? Continue { get; set; }
        public string Token() => Keys.Token(claims =>
        {
            claims["aud"] = ClientId;
            claims["sub"] = "human-subject";
            claims["email"] = Email;
            claims["exp"] = Clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds();
            Claims?.Invoke(claims);
        }, WrongSignature);
    }

    private sealed class SessionSdk(State state) : AmazonRDSDataServiceClient(
        new Amazon.Runtime.BasicAWSCredentials("hermetic", "hermetic"), new AmazonRDSDataServiceConfig { RegionEndpoint = Amazon.RegionEndpoint.EUWest1 })
    {
        public override Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken cancellationToken = default)
        {
            lock (state.Statements) state.Statements.Add(request.Sql);
            throw new DatabaseResumingException("fake-sensitive-aurora-secret");
        }
    }

    private sealed class SessionLogs(State state) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(state, categoryName);
        public void Dispose() { }
        private sealed class Logger(State state, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState value) where TState : notnull
            {
                state.Logs.Enqueue($"{category} scope: {value}");
                return null;
            }
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState value, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                state.Logs.Enqueue($"{category}: {formatter(value, exception)}");
                if (exception is not null) state.LoggedExceptions.Enqueue(exception);
            }
        }
    }

    private sealed class KeySecrets(State state) : ISecretsProvider
    {
        public Task<SecretsPayload> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(state.KeyPayload is null ? new SecretsPayload { UserSessionEncryptionKey = state.Key }
                : JsonSerializer.Deserialize<SecretsPayload>(state.KeyPayload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
    }

    private sealed class RejectLegacyGoogleTransport(State state) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            state.UnexpectedGoogleRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("{}") });
        }
    }

    private sealed class KeyTransport(State state) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (state.KeyFailure) throw new HttpRequestException("fake JWKS failure");
            return new HttpMessageInvoker(state.Keys, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }

    private sealed class TokenTransport(State state) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.ToString());
            var parsed = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
            lock (state.Forms) state.Forms.Add(parsed.AllKeys.ToDictionary(key => key!, key => parsed[key]));
            var status = state.Status;
            var networkFailure = state.NetworkFailure;
            var timeoutFailure = state.TimeoutFailure;
            var body = state.Body ?? JsonSerializer.Serialize(new { id_token = state.Token(), access_token = "fake-access-token",
                expires_in = 3600, refresh_token = state.RefreshCredential });
            var entered = state.Entered;
            var continuation = state.Continue;
            if (entered is not null)
            {
                entered.TrySetResult();
                await continuation!.Task.WaitAsync(cancellationToken);
            }
            if (timeoutFailure) throw new TaskCanceledException("fake Google timeout");
            if (networkFailure) throw new HttpRequestException("fake network outage with fake-refresh-credential");
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

}
