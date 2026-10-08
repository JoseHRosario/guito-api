using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using GuitoApi.Configuration;
using GuitoApi.Infrastructure.EnableBanking;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GuitoApi.Tests;

/// <summary>
/// Enable Banking JWT client-assertion signer (issue #89, ADR-0004). Every EB request
/// carries a fresh JWT: header {typ=JWT, alg, kid=application id}, body {iss, aud, iat, exp}.
/// </summary>
public class EnableBankingJwtSignerTests
{
    private static readonly string PrivateKeyPem = MakePrivateKeyPem();

    private static EnableBankingSecrets Secrets() => new()
    {
        ApplicationId = "0f8c0f7a-6f3e-4d9a-9f2e-2b0b4d1c3e5a",
        PrivateKey = PrivateKeyPem,
    };

    private static EnableBankingJwtSigner Signer(string algorithm) => new(
        Microsoft.Extensions.Options.Options.Create(new EnableBankingOptions { JwtAlgorithm = algorithm }),
        new FakeSecretsProvider { Payload = { EnableBanking = Secrets() } });

    private static async Task<JwtSecurityToken> ReadToken(string algorithm)
    {
        var signer = Signer(algorithm);
        return new JwtSecurityTokenHandler().ReadJwtToken(await signer.CreateTokenAsync());
    }

    private static RsaSecurityKey PublicKeyVerificationKey()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PrivateKeyPem);
        return new RsaSecurityKey(rsa.ExportParameters(false));
    }

    [Fact]
    public async Task CreateTokenAsync_ShouldCarryIssuerAudienceKidAndHeader_WhenPs256Requested()
    {
        // Arrange / Act
        var jwt = await ReadToken("PS256");

        // Assert
        Assert.Equal("PS256", jwt.Header.Alg);
        Assert.Equal("JWT", jwt.Header.Typ);
        Assert.Equal(Secrets().ApplicationId, jwt.Header.Kid);
        Assert.Equal("enablebanking.com", jwt.Issuer);
        Assert.Contains("api.enablebanking.com", jwt.Audiences);
    }

    [Fact]
    public async Task CreateTokenAsync_ShouldKeepTokenUnderEbMaxTtl_WhenCalled()
    {
        // Arrange / Act — EB rejects tokens with TTL above 86400 seconds (24h).
        var jwt = await ReadToken("RS256");

        // Assert
        var ttl = jwt.ValidTo - jwt.IssuedAt;
        Assert.True(ttl <= TimeSpan.FromSeconds(86400));
        Assert.True(ttl > TimeSpan.Zero);
    }

    [Fact]
    public async Task CreateTokenAsync_ShouldProduceVerifiableSignature_WhenPs256Requested()
    {
        // Arrange — PS256 is RSA-PSS; the public half of the test key must verify it.
        var signer = Signer("PS256");
        var token = await signer.CreateTokenAsync();

        // Act
        var handler = new JwtSecurityTokenHandler();
        handler.ValidateToken(token, ValidationParameters(), out _);

        // Assert
        Assert.Equal("PS256", handler.ReadJwtToken(token).Header.Alg);
    }

    [Fact]
    public async Task CreateTokenAsync_ShouldProduceVerifiableSignature_WhenRs256Requested()
    {
        // Arrange — RS256 is the empirically verified default (EB sandbox, 2026-10-08:
        // PS256 → 401 "Wrong signature", RS256 → 200); PS256 stays selectable via config.
        var signer = Signer("RS256");
        var token = await signer.CreateTokenAsync();

        // Act
        var handler = new JwtSecurityTokenHandler();
        handler.ValidateToken(token, ValidationParameters(), out _);

        // Assert
        Assert.Equal("RS256", handler.ReadJwtToken(token).Header.Alg);
    }

    private static TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = "enablebanking.com",
        ValidAudience = "api.enablebanking.com",
        IssuerSigningKey = PublicKeyVerificationKey(),
        ValidateIssuerSigningKey = true,
        ValidateLifetime = false,
    };

    private static string MakePrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}