namespace GuitoApi.Model;

/// <summary>Guito's review decision, independent of the bank's booking status.</summary>
public enum MatchStatus
{
    NeedsReview,
    Matched,
    Ignored
}
