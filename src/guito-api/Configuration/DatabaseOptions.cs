namespace GuitoApi.Configuration;

/// <summary>
/// Binding for the root "Database" config section (issue #87, ADR-0013). The RDS Data API
/// needs no TCP connection string: it addresses the cluster by ResourceArn and authenticates
/// with the per-environment db secret (guito-api/db-dev / db-staging / db-prod, issue #80).
/// </summary>
public class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>The Aurora cluster ARN (Data API resource).</summary>
    public string ResourceArn { get; set; } = string.Empty;

    /// <summary>Full ARN of the environment's db secret (runtime credentials).</summary>
    public string SecretArn { get; set; } = string.Empty;

    /// <summary>The environment's database inside the shared cluster (guito_dev / _staging / _prod).</summary>
    public string DatabaseName { get; set; } = string.Empty;
}