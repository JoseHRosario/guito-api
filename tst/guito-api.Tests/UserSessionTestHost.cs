using System.Net;
using System.Text.Json;
using GuitoApi.Configuration;
using GuitoApi.Infrastructure.Auth;
using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Infrastructure.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
            services.PostConfigure<AppConfigurationOptions>(options =>
            {
                options.Authentication.ValidateApiKey = true;
                options.Authentication.ValidateIdToken = true;
                options.Authentication.GoogleClientId = ClientId;
                options.Authentication.OAuthAudience = ClientId;
                options.Authentication.AllowedLogins = [Email];
                options.Cors.AllowedOrigins = [Origin];
            });
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(state.Clock);
            services.RemoveAll<ISecretsProvider>();
            services.AddSingleton<ISecretsProvider>(new KeySecrets(state));
            services.RemoveAll<IHumanAuthSecretProvider>();
            services.AddSingleton<IHumanAuthSecretProvider>(new FakeHumanAuthSecretProvider());
            services.RemoveAll<IPostgresDataApiClient>();
            services.AddSingleton<IPostgresDataApiClient>(state.Database);
            services.AddHttpClient(GoogleSessionProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new TokenTransport(state));
            services.AddHttpClient(GuitoApi.Services.Auth.RevokeGoogleTokenService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new RejectLegacyGoogleTransport(state));
            services.AddHttpClient(GuitoApi.Services.Auth.GoogleTokenExchangeService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new RejectLegacyGoogleTransport(state));
            services.AddHttpClient("SheetsReportingJwks").ConfigurePrimaryHttpMessageHandler(() => state.Keys);
            services.AddHttpClient("UserSessionJwks").ConfigurePrimaryHttpMessageHandler(() => new KeyTransport(state));
        });
    }

    internal sealed class State
    {
        public Clock Clock { get; } = new();
        public Database Database { get; } = new();
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

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
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
            if (state.Entered is not null)
            {
                state.Entered.TrySetResult();
                await state.Continue!.Task.WaitAsync(cancellationToken);
            }
            if (timeoutFailure) throw new TaskCanceledException("fake Google timeout");
            if (networkFailure) throw new HttpRequestException("fake network outage with fake-refresh-credential");
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    internal sealed class Database : IPostgresDataApiClient
    {
        private readonly Dictionary<string, Dictionary<string, string?>> _rows = [];
        public List<string> CredentialsWritten { get; } = [];
        public bool Fail { get; set; }
        public bool CorruptCredential { get; set; }

        public Task<PostgresResult> ExecuteAsync(string sql, IReadOnlyList<PostgresParameter>? parameters = null,
            string? transactionId = null, CancellationToken cancellationToken = default)
        {
            lock (_rows) return Task.FromResult(Execute(sql, parameters!));
        }

        private PostgresResult Execute(string sql, IReadOnlyList<PostgresParameter> parameters)
        {
            if (Fail) throw new InvalidOperationException("fake database outage fake-refresh-credential");
            Assert.Contains("user_sessions", sql);
            var values = parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value.StringValue);
            var hash = values["hash"]!;
            _rows.TryGetValue(hash, out var row);
            if (sql.StartsWith("INSERT")) return Insert(hash, values);
            if (sql.StartsWith("SELECT")) return Select(hash, row, values);
            if (sql.StartsWith("DELETE")) return Delete(sql, hash, row, values);
            if (row is null) return Result();
            if (sql.Contains("RETURNING subject")) return AcquireLease(sql, row, values);
            if (sql.Contains("refresh_lease = :lease") && row.GetValueOrDefault("lease") != values["lease"]) return Result();
            return sql.Contains("encrypted_credential = :credential") ? CompleteLease(sql, row, values) : ReleaseLease(row);
        }

        private PostgresResult Insert(string hash, Dictionary<string, string?> values)
        {
            Assert.DoesNotContain("fake-refresh-credential", string.Join(" ", values.Values));
            CredentialsWritten.Add(values["credential"]!);
            _rows.Add(hash, new(values));
            return Result(1);
        }

        private static PostgresResult Select(string hash, Dictionary<string, string?>? row, Dictionary<string, string?> values) =>
            row is not null && Date(row, "expires") > Date(values, "now") ? Result(0, [hash]) : Result();

        private PostgresResult Delete(string sql, string hash, Dictionary<string, string?>? row, Dictionary<string, string?> values)
        {
            if (row is null || (sql.Contains("refresh_lease = :lease") && row.GetValueOrDefault("lease") != values["lease"])) return Result();
            if (sql.Contains("expires_at <=") && Date(row, "expires") > Date(values, "now")) return Result();
            _rows.Remove(hash);
            return Result(1);
        }

        private PostgresResult AcquireLease(string sql, Dictionary<string, string?> row, Dictionary<string, string?> values)
        {
            if (sql.Contains("expires_at >") && Date(row, "expires") <= Date(values, "now")) return Result();
            if (sql.Contains("refresh_lease IS NULL") && row.GetValueOrDefault("lease") is not null && Date(row, "lease_expires") > Date(values, "now")) return Result();
            row["lease"] = values["lease"];
            row["lease_expires"] = values["lease_expires"];
            return Result(1, [row["subject"]!, row["email"]!, CorruptCredential ? "v1.corrupt" : row["credential"]!, row["expires"]!]);
        }

        private PostgresResult CompleteLease(string sql, Dictionary<string, string?> row, Dictionary<string, string?> values)
        {
            if (sql.Contains("expires_at >") && Date(row, "expires") <= Date(values, "now")) return Result();
            if (sql.Contains("lease_expires_at >") && Date(row, "lease_expires") <= Date(values, "now")) return Result();
            row["credential"] = values["credential"];
            row["expires"] = values["expires"];
            CredentialsWritten.Add(values["credential"]!);
            return ReleaseLease(row);
        }

        private static PostgresResult ReleaseLease(Dictionary<string, string?> row)
        {
            row["lease"] = null;
            row["lease_expires"] = null;
            return Result(1);
        }

        private static DateTimeOffset Date(Dictionary<string, string?> values, string key) => DateTimeOffset.Parse(values[key]!);
        private static PostgresResult Result(long changed = 0, string[]? row = null) => new([], row is null ? [] : [row.Select(PostgresValue.FromString).ToArray()], changed);
        public Task<string> BeginTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CommitAsync(string transactionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RollbackAsync(string transactionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
