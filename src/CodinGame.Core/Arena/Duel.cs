using CodinGame.Core.Config;
using CodinGame.Core.Judging;
using Microsoft.Extensions.Logging;

namespace CodinGame.Core.Arena;

public sealed record DuelOutcome(int Wins, int Losses, int Draws, SprtDecision Decision, double Llr, string? CandidateFault)
{
    public int Games => Wins + Losses + Draws;
}

/// <summary>A duel game already journaled: its seed, the candidate's seat, its result for the candidate (<see cref="Results"/>), its fault.</summary>
public sealed record PlayedGame(long Seed, int Seat, string Result, string? Fault);

/// <summary>
/// Fait s'affronter le candidat et le champion dans le simulateur, jusqu'à ce que le test
/// séquentiel tranche. Chaque graine (donc chaque circuit) est jouée deux fois, places
/// échangées, pour que l'avantage d'une place ne compte pas. Les graines vont de 1 à N :
/// tous les essais sont mesurés sur les mêmes circuits. Une faute du candidat arrête tout.
/// Second round: a duel that reaches its cap without a verdict, with a candidate that leads,
/// goes on with a lower bar (<c>arene.elo1_prolongation</c>) up to <c>arene.parties_prolongation</c>
/// games. Near the top, gains are small: 52 to 53 % of wins needs about 2,000 games to be proven.
/// A duel stopped by a restart goes on from its journaled games (<c>played</c>), in the round it was in.
/// </summary>
public sealed class Duel(RefereeGame game, ArenaSettings settings, ILogger log)
{
    public async Task<DuelOutcome> RunAsync(
        string candidateExe,
        string championExe,
        Action<long, int, MatchResult>? record,
        CancellationToken ct,
        bool relay = false,
        IReadOnlyList<PlayedGame>? played = null)
    {
        var sprt = new Sprt(settings.Elo0, settings.Elo1, settings.Alpha, settings.Beta);
        var second = new Sprt(settings.Elo0, settings.Elo1Prolongation, settings.Alpha, settings.Beta);
        var limits = settings.Limits();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var slots = new SemaphoreSlim(settings.Parallele);
        var gate = new Lock();
        int wins = 0, losses = 0, draws = 0;
        string? fault = null;
        var decision = SprtDecision.Continue;
        var running = new List<Task>();
        var limit = settings.PartiesMax;
        var k = 0;

        // Game k is seed k / 2 + 1 with the candidate in seat k % 2: the games already played are skipped.
        var done = new HashSet<long>();
        foreach (var g in played ?? [])
        {
            done.Add(((g.Seed - 1) * 2) + g.Seat);
            if (g.Fault is not null)
            {
                fault ??= g.Fault;
            }
            else if (g.Result == Results.Draw)
            {
                draws++;
            }
            else if (g.Result == Results.Win)
            {
                wins++;
            }
            else
            {
                losses++;
            }
        }

        if (done.Count > settings.PartiesMax && settings.PartiesProlongation > settings.PartiesMax)
        {
            // Only a second round goes past the first round's cap.
            sprt = second;
            limit = settings.PartiesProlongation;
        }

        if (done.Count >= settings.PartiesMin)
        {
            decision = sprt.Decide(wins, losses);
        }

        if (done.Count > 0)
        {
            log.LogInformation("Duel repris : {Wins} V / {Losses} D / {Draws} N déjà joués{Round}", wins, losses, draws,
                limit > settings.PartiesMax ? ", en deuxième manche" : "");
        }

        if (fault is not null || decision != SprtDecision.Continue)
        {
            stop.Cancel();
        }

        while (true)
        {
        for (; k < limit; k++)
        {
            if (done.Contains(k))
            {
                continue;
            }

            try
            {
                await slots.WaitAsync(stop.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }

            var seed = (k / 2) + 1L;
            var candidateSeat = k % 2;
            running.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await PlayOneAsync(seed, candidateSeat, candidateExe, championExe, limits, relay, stop.Token);
                    Telemetry.LocalGames.Add(1);
                    lock (gate)
                    {
                        if (decision != SprtDecision.Continue || fault is not null)
                        {
                            return;
                        }

                        record?.Invoke(seed, candidateSeat, result);
                        if (result.Faults[candidateSeat] != FaultKind.None)
                        {
                            fault = result.Detail ?? result.Faults[candidateSeat].ToString();
                            stop.Cancel();
                            return;
                        }

                        if (result.IsDraw)
                        {
                            draws++;
                        }
                        else if (result.Winner == candidateSeat)
                        {
                            wins++;
                        }
                        else
                        {
                            losses++;
                        }

                        if (wins + losses + draws >= settings.PartiesMin)
                        {
                            decision = sprt.Decide(wins, losses);
                            if (decision != SprtDecision.Continue)
                            {
                                stop.Cancel();
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // Partie abandonnée : le duel est déjà tranché.
                }
                finally
                {
                    slots.Release();
                }
            }, CancellationToken.None));
        }

        await Task.WhenAll(running);
        ct.ThrowIfCancellationRequested();

        if (fault is null && decision == SprtDecision.Continue && ShouldProlong(settings, limit, second.Llr(wins, losses)))
        {
            log.LogInformation("Deuxième manche, de {De} à {A} parties avec une barre plus basse (+{Elo} Elo) : le candidat mène ({Wins} V / {Losses} D, LLR {Llr:F2})",
                limit, settings.PartiesProlongation, settings.Elo1Prolongation, wins, losses, second.Llr(wins, losses));
            sprt = second;
            limit = settings.PartiesProlongation;
            decision = sprt.Decide(wins, losses);
            if (decision == SprtDecision.Continue)
            {
                continue;
            }
        }

        break;
        }

        if (fault is null && decision == SprtDecision.Continue)
        {
            // Plafond de parties atteint sans verdict : l'écart, s'il existe, est trop petit pour être mesuré.
            decision = SprtDecision.NotBetter;
        }

        var outcome = new DuelOutcome(wins, losses, draws, fault is null ? decision : SprtDecision.NotBetter, sprt.Llr(wins, losses), fault);
        log.LogInformation("Duel terminé : {Wins} V / {Losses} D / {Draws} N, LLR {Llr:F2}, décision {Decision}{Fault}",
            wins, losses, draws, outcome.Llr, outcome.Decision, fault is null ? "" : $", faute du candidat : {fault}");
        return outcome;
    }

    /// <summary>A second round for a duel at its cap without a verdict? Only once, when the candidate leads by the second round's test.</summary>
    internal static bool ShouldProlong(ArenaSettings settings, int limit, double llr) =>
        limit < settings.PartiesProlongation && llr >= settings.LlrProlongation;

    private async Task<MatchResult> PlayOneAsync(
        long seed, int candidateSeat, string candidateExe, string championExe, TurnLimits limits, bool relay, CancellationToken ct)
    {
        await using var candidate = BotProcess.Start(candidateExe);
        await using var champion = BotProcess.Start(championExe);
        IBotConnection[] bots = candidateSeat == 0 ? [candidate, champion] : [champion, candidate];
        return await game.PlayAsync(bots, seed, limits, ct, relay);
    }
}
