using CodinGame.Core.Data;

namespace CodinGame.Core.Pipeline;

/// <summary>
/// Closes the loop on the real leaderboard: a champion whose fully ranked score is clearly below
/// the best recent champion's is a false progress, and that champion comes back. A fixed rule,
/// not an AI.
/// </summary>
public static class RankingCheck
{
    /// <summary>
    /// The champion to withdraw and the one to restore, or null. Only the current champion can be
    /// withdrawn, and only when its score is lower by more than <paramref name="tolerance"/> points
    /// (0 disables the rule) than the best champion still standing among the previous one and those
    /// promoted in the <paramref name="window"/> before it.
    /// Comparing with the best, not only the previous one, stops a slow slide: champions each a little
    /// below the one before never trigger the rule, though together they lose more than the tolerance
    /// (2026-11-25: 41.77, then 41.00, 41.32, 41.41, 40.64). A bot withdrawn once is never restored,
    /// so two former champions cannot take turns while the whole leaderboard drifts.
    /// </summary>
    public static (ScoredChampion Worse, ScoredChampion Restore)? Regression(
        IReadOnlyList<ScoredChampion> scored, string? currentHash, double tolerance, TimeSpan window)
    {
        if (tolerance <= 0 || scored.Count == 0)
        {
            return null;
        }

        var withdrawn = scored.Where(c => c.Retired).Select(c => c.Hash).ToHashSet();
        var standing = scored.Where(c => !c.Retired).ToList();
        if (standing.Count == 0 || standing[^1] is not { } last || last.Hash != currentHash)
        {
            return null;
        }

        var others = standing.Take(standing.Count - 1)
            .Where(c => c.Hash != last.Hash && !withdrawn.Contains(c.Hash))
            .ToList();
        if (others.Count == 0)
        {
            return null;
        }

        var from = others[^1].Since - window;
        var best = others.Where(c => c.Since >= from).MaxBy(c => c.Score)!;
        return last.Score < best.Score - tolerance ? (last, best) : null;
    }
}
