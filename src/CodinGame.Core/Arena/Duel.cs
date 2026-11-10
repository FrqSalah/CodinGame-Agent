using CodinGame.Core.Config;
using CodinGame.Core.Judging;
using Microsoft.Extensions.Logging;

namespace CodinGame.Core.Arena;

public sealed record DuelOutcome(int Wins, int Losses, int Draws, SprtDecision Decision, double Llr, string? CandidateFault)
{
    public int Games => Wins + Losses + Draws;
}

/// <summary>
/// Fait s'affronter le candidat et le champion dans le simulateur, jusqu'à ce que le test
/// séquentiel tranche. Chaque graine (donc chaque circuit) est jouée deux fois, places
/// échangées, pour que l'avantage d'une place ne compte pas. Les graines vont de 1 à N :
/// tous les essais sont mesurés sur les mêmes circuits. Une faute du candidat arrête tout.
/// </summary>
public sealed class Duel(RefereeGame game, ArenaSettings settings, ILogger log)
{
    public async Task<DuelOutcome> RunAsync(
        string candidateExe,
        string championExe,
        Action<long, int, MatchResult>? record,
        CancellationToken ct)
    {
        var sprt = new Sprt(settings.Elo0, settings.Elo1, settings.Alpha, settings.Beta);
        var limits = new TurnLimits(
            TimeSpan.FromMilliseconds(settings.PremierTourMs * settings.ToleranceTemps),
            TimeSpan.FromMilliseconds(settings.TourMs * settings.ToleranceTemps));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var slots = new SemaphoreSlim(settings.Parallele);
        var gate = new Lock();
        int wins = 0, losses = 0, draws = 0;
        string? fault = null;
        var decision = SprtDecision.Continue;
        var running = new List<Task>();

        for (var k = 0; k < settings.PartiesMax; k++)
        {
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
                    var result = await PlayOneAsync(seed, candidateSeat, candidateExe, championExe, limits, stop.Token);
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

    private async Task<MatchResult> PlayOneAsync(
        long seed, int candidateSeat, string candidateExe, string championExe, TurnLimits limits, CancellationToken ct)
    {
        await using var candidate = BotProcess.Start(candidateExe);
        await using var champion = BotProcess.Start(championExe);
        IBotConnection[] bots = candidateSeat == 0 ? [candidate, champion] : [champion, candidate];
        return await game.PlayAsync(bots, seed, limits, ct);
    }
}
