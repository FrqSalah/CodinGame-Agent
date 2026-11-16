using CodinGame.Core.Data;

namespace CodinGame.Core.Pipeline;

/// <summary>
/// Confirmation sur CodinGame contre de vrais joueurs : choix des adversaires (une règle fixe, pas
/// une IA) et comparaison du candidat au champion, partie par partie, sur les mêmes circuits.
/// </summary>
public static class Confirmation
{
    /// <summary>
    /// Choisit <paramref name="count"/> joueurs classés au-dessus de nous, au plus à
    /// <paramref name="span"/> rangs, répartis régulièrement sur cet écart. S'il en manque (nous sommes
    /// presque premiers), les plus proches en dessous complètent.
    /// </summary>
    public static IReadOnlyList<string> PickOpponents(IReadOnlyList<(string AgentId, int Rank)> players, int myRank, int count, int span)
    {
        var above = players.Where(p => p.Rank < myRank && p.Rank >= myRank - span).OrderBy(p => p.Rank).ToList();
        if (above.Count >= count)
        {
            return count == 1
                ? [above[^1].AgentId]
                : Enumerable.Range(0, count).Select(i => above[i * (above.Count - 1) / (count - 1)].AgentId).Distinct().ToList();
        }

        var below = players.Where(p => p.Rank > myRank).OrderBy(p => p.Rank).Take(count - above.Count);
        return above.Concat(below).Select(p => p.AgentId).ToList();
    }

    /// <summary>
    /// Compare les parties jouées sur les mêmes circuits contre les mêmes adversaires : parties que
    /// seul le candidat gagne (plus), que seul le champion gagne (moins), et victoires de chacun.
    /// </summary>
    public static (int Plus, int Minus, int CandidateWins, int ChampionWins) Compare(
        IReadOnlyList<ApiGameEntry> champion, IReadOnlyList<ApiGameEntry> candidate)
    {
        int plus = 0, minus = 0, cw = 0, chw = 0;
        for (var i = 0; i < Math.Min(champion.Count, candidate.Count); i++)
        {
            var a = candidate[i].Outcome == nameof(CodinGameApi.Outcome.Win);
            var b = champion[i].Outcome == nameof(CodinGameApi.Outcome.Win);
            cw += a ? 1 : 0;
            chw += b ? 1 : 0;
            plus += a && !b ? 1 : 0;
            minus += b && !a ? 1 : 0;
        }

        return (plus, minus, cw, chw);
    }
}
