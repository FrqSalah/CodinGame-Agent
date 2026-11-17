using CodinGame.Core.Data;

namespace CodinGame.Core.Pipeline;

/// <summary>
/// Closes the loop on the real leaderboard: a champion whose fully ranked score is clearly below
/// the previous champion's is a false progress, and the previous one comes back. A fixed rule,
/// not an AI.
/// </summary>
public static class RankingCheck
{
    /// <summary>
    /// The champion to withdraw and the one to restore, or null. Only the current champion can be
    /// withdrawn, only against the previous champion still standing, and only when its score is
    /// lower by more than <paramref name="tolerance"/> points (0 disables the rule).
    /// </summary>
    public static (ScoredChampion Worse, ScoredChampion Restore)? Regression(
        IReadOnlyList<ScoredChampion> scored, string? currentHash, double tolerance)
    {
        if (tolerance <= 0)
        {
            return null;
        }

        var standing = scored.Where(c => !c.Retired).ToList();
        if (standing.Count < 2)
        {
            return null;
        }

        var last = standing[^1];
        var previous = standing[^2];
        if (last.Hash != currentHash || last.Hash == previous.Hash)
        {
            return null;
        }

        return last.Score < previous.Score - tolerance ? (last, previous) : null;
    }
}
