namespace GuitoApi.Model;

/// <summary>
/// The Jev-suggested category attached to a bank transaction (issue #112): the mirror
/// row's id + name. Null on a transaction whose suggestion failed or was never resolved.
/// </summary>
public sealed record BankSuggestedCategory(long Id, string Name);
