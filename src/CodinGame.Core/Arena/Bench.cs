using CodinGame.Core.Config;
using Microsoft.Extensions.Logging;

namespace CodinGame.Core.Arena;

/// <summary>Une situation du banc : un adversaire extérieur, un circuit (graine) et une place.</summary>
public sealed record BenchCase(string Opponent, long Seed, int Seat);

/// <summary>
/// Le banc d'adversaires extérieurs : des bots publiés par d'autres joueurs, de niveau connu.
/// Chaque bot mesuré y joue les mêmes situations, ce qui permet de comparer candidat et
/// champion situation par situation. Sans ce banc, le juge ne compare un candidat qu'au
/// champion, et le bot finit par ne progresser que contre ses propres versions.
/// </summary>
public sealed class Bench(RefereeGame game, ArenaSettings arena, ILogger log)
{
    public static IReadOnlyList<BenchCase> Cases(IEnumerable<string> opponents, int gamesPerOpponent) =>
        opponents.SelectMany(o => Enumerable.Range(0, gamesPerOpponent).Select(k => new BenchCase(o, (k / 2) + 1L, k % 2))).ToList();

    /// <summary>Joue les situations qui manquent pour ce bot. <paramref name="known"/> donne celles déjà au journal.</summary>
    public async Task PlayAsync(
        string botExe,
        IReadOnlyDictionary<string, string> opponentExes,
        IReadOnlyList<BenchCase> cases,
        IReadOnlyDictionary<BenchCase, string> known,
        Action<BenchCase, MatchResult> record,
        CancellationToken ct)
    {
        var limits = new TurnLimits(
            TimeSpan.FromMilliseconds(arena.PremierTourMs * arena.ToleranceTemps),
            TimeSpan.FromMilliseconds(arena.TourMs * arena.ToleranceTemps));
        using var slots = new SemaphoreSlim(arena.Parallele);
        var todo = cases.Where(c => !known.ContainsKey(c)).ToList();
        log.LogInformation("Banc : {N} partie(s) à jouer ({Deja} déjà connues)", todo.Count, cases.Count - todo.Count);

        await Task.WhenAll(todo.Select(async c =>
        {
            await slots.WaitAsync(ct);
            try
            {
                await using var bot = BotProcess.Start(botExe);
                await using var opponent = BotProcess.Start(opponentExes[c.Opponent]);
                IBotConnection[] bots = c.Seat == 0 ? [bot, opponent] : [opponent, bot];
                var result = await game.PlayAsync(bots, c.Seed, limits, ct);
                Telemetry.LocalGames.Add(1);
                record(c, result);
            }
            finally
            {
                slots.Release();
            }
        }));
    }

    /// <summary>
    /// Compare deux bots situation par situation : <c>Plus</c> compte les situations gagnées par le
    /// candidat et perdues par le champion, <c>Minus</c> l'inverse.
    /// </summary>
    public static (int Plus, int Minus, int CandidateWins, int ChampionWins) Compare(
        IReadOnlyList<BenchCase> cases,
        IReadOnlyDictionary<BenchCase, string> candidate,
        IReadOnlyDictionary<BenchCase, string> champion)
    {
        int plus = 0, minus = 0, cw = 0, chw = 0;
        foreach (var c in cases)
        {
            var a = candidate[c] == Results.Win;
            var b = champion[c] == Results.Win;
            cw += a ? 1 : 0;
            chw += b ? 1 : 0;
            plus += a && !b ? 1 : 0;
            minus += b && !a ? 1 : 0;
        }

        return (plus, minus, cw, chw);
    }
}

/// <summary>Résultats d'une partie, du point de vue du bot mesuré, tels qu'écrits au journal.</summary>
public static class Results
{
    public const string Win = "victoire";
    public const string Loss = "defaite";
    public const string Draw = "egalite";

    public static string For(MatchResult r, int seat) => r.IsDraw ? Draw : r.Winner == seat ? Win : Loss;
}
