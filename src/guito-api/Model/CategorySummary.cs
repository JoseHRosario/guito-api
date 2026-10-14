namespace GuitoApi.Model;

/// <summary>
/// An authoritative category with its immutable Int64 identity and optional description.
/// </summary>
public sealed record CategorySummary(long Id, string Name, string? Description);
