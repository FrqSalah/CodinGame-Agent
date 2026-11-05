using System.Diagnostics;

namespace CodinGame.Core.Arena;

/// <summary>
/// Résultat d'un match unique (une direction d'appariement).
/// </summary>
public sealed record MatchResult(
    string Player1Path,
    string Player2Path,
    long Seed,
    int Winner,          // 1 ou 2 ; 0 = draw
    bool Player1Timeout,
    bool Player2Timeout,
    bool Player1Crash,
    bool Player2Crash,
    string Stdout,
    string Stderr,
    TimeSpan Duration
);

/// <summary>
/// Résultat d'une paire appariée (même seed, places échangées).
/// Voir .Agent/DETERMINISTIC_LAYER.md §2.A (appariement).
/// </summary>
public sealed record PairedMatchResult(
    MatchResult Forward,
    MatchResult Reverse,
    long Seed
);

/// <summary>
/// Exécuteur de matchs entre deux bots via un referee.
/// Duels 1v1 uniquement (périmètre v1).
///
/// Le MatchRunner ne choisit pas ses cœurs. Il reçoit un créneau de l'ArenaScheduler
/// et applique Process.ProcessorAffinity sur ce créneau.
///
/// Voir .Agent/DETERMINISTIC_LAYER.md §2.A.
/// </summary>
public sealed class MatchRunner
{
    /// <summary>
    /// Exécute une paire appariée : même seed, places échangées.
    /// </summary>
    public async Task<PairedMatchResult> RunPairedMatchAsync(
        string refereePath,
        string bot1Path,
        string bot2Path,
        long seed,
        GameTimeoutConfig timeouts,
        ArenaSlot slot,
        CancellationToken ct = default)
    {
        var forward = await RunSingleMatchAsync(refereePath, bot1Path, bot2Path, seed, timeouts, slot, ct);
        var reverse = await RunSingleMatchAsync(refereePath, bot2Path, bot1Path, seed, timeouts, slot, ct);
        return new PairedMatchResult(forward, reverse, seed);
    }

    private async Task<MatchResult> RunSingleMatchAsync(
        string refereePath,
        string player1Path,
        string player2Path,
        long seed,
        GameTimeoutConfig timeouts,
        ArenaSlot slot,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // TODO Phase 2 : implémenter le lancement réel du referee en sous-processus
        // avec les contraintes suivantes (voir DETERMINISTIC_LAYER.md §2.A) :
        //   - Process.ProcessorAffinity = slot.AffinityMask
        //   - Timeout du premier tour = timeouts.FirstTurnTimeoutMs
        //   - Timeout par tour = timeouts.TurnTimeoutMs
        //   - Capture asynchrone de stdout/stderr
        //   - Détection de crash (exit code, SIGSEGV)
        //   - Un pipeline sans timeout configuré refuse de démarrer

        await Task.Delay(1, ct); // placeholder

        sw.Stop();
        return new MatchResult(
            Player1Path: player1Path,
            Player2Path: player2Path,
            Seed: seed,
            Winner: 0,
            Player1Timeout: false,
            Player2Timeout: false,
            Player1Crash: false,
            Player2Crash: false,
            Stdout: string.Empty,
            Stderr: string.Empty,
            Duration: sw.Elapsed
        );
    }
}
