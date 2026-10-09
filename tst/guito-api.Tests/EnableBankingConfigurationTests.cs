using System.Text.Json;

namespace GuitoApi.Tests;

/// <summary>
/// Config-shape parity of the deployed environment files (issue #118): the EB
/// block must be complete wherever BankProvider is EnableBanking, so a missing
/// key fails a test here instead of a 500 in the live environment (the prod 500
/// 'AuthCallbackUrl is required' motivated this). Hermetic — reads the JSON
/// files shipped in the repo, no AWS access.
/// </summary>
public class EnableBankingConfigurationTests
{
    private static readonly string SrcRoot = FindSrcRoot();

    [Fact]
    public void Staging_carries_the_complete_EnableBanking_block()
    {
        AssertRequiredKeys(Path.Combine(SrcRoot, "appsettings.Staging.json"));
    }

    [Fact]
    public void Production_carries_the_complete_EnableBanking_block()
    {
        var doc = AssertRequiredKeys(Path.Combine(SrcRoot, "appsettings.Production.json"));
        // prod speaks to the real EB application with the PROD callback, never the staging one
        Assert.Equal("EnableBanking", doc.RootElement.GetProperty("AppConfiguration").GetProperty("BankProvider").GetString());
        Assert.Equal("https://guito.api.kerumirembora.com/BankAuth/callback",
            doc.RootElement.GetProperty("AppConfiguration").GetProperty("EnableBanking").GetProperty("AuthCallbackUrl").GetString());
    }

    private static JsonDocument AssertRequiredKeys(string path)
    {
        Assert.True(File.Exists(path), $"missing config file: {path}");
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        var app = doc.RootElement.GetProperty("AppConfiguration");
        Assert.Equal("EnableBanking", app.GetProperty("BankProvider").GetString());
        var eb = app.GetProperty("EnableBanking");
        foreach (var key in new[] { "AuthCallbackUrl", "ApplicationId", "SecretsSource", "SecretsManagerSecretName" })
        {
            Assert.True(eb.TryGetProperty(key, out var value) && !string.IsNullOrWhiteSpace(value.GetString()),
                $"{Path.GetFileName(path)}: EnableBanking.{key} is required for the bank auth flow.");
        }
        return doc;
    }

    private static string FindSrcRoot()
    {
        // walk up from the test assembly's repo layout: tst/guito-api.Tests → src/guito-api
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "src", "guito-api");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new InvalidOperationException("src/guito-api not found above the test run directory.");
    }
}