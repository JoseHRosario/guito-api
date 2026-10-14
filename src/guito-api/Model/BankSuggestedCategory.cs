namespace GuitoApi.Model;

/// <summary>
/// The Jev-suggested category identity and name attached to a bank transaction.
/// Null on a transaction whose suggestion failed or was never resolved.
/// </summary>
public sealed record BankSuggestedCategory(long Id, string Name);
