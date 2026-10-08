namespace GuitoApi.DataTransferObjects.Output;

/// <summary>
/// The Jev-suggested category on a pending bank transaction (issue #112): id is the
/// categories mirror key, name the Sheets category name. Null when the row carries
/// no suggestion.
/// </summary>
public sealed record SuggestedCategory(long Id, string Name);
