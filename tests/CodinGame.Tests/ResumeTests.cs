using CodinGame.Core.Arena;
using CodinGame.Core.Config;
using CodinGame.Core.Data;
using CodinGame.Core.Judging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodinGame.Tests;

/// <summary>
/// A deploy restarts the farm: the trial it interrupted goes on from its journaled duel games,
/// instead of being lost. Here no engine runs: a duel that needs no more games must play none.
/// </summary>
public class ResumeTests
{
    private static readonly ArenaSettings Settings =
        new("absent-engine", 2, 1000, 75, 3.0, 20, 600, 0, 40, 0.05, 0.05, PartiesProlongation: 3000, LlrProlongation: 0, Elo1Prolongation: 20);

    private static Task<DuelOutcome> ResumeAsync(IReadOnlyList<PlayedGame> played) =>
        new Duel(new RefereeGame("absent-engine"), Settings, NullLogger.Instance)
            .RunAsync("candidate", "champion", null, TestContext.Current.CancellationToken, played: played);

    /// <summary>Games 0 to n-1 of a duel: seed k / 2 + 1, candidate in seat k % 2; the first <paramref name="wins"/> won.</summary>
    private static List<PlayedGame> Games(int wins, int losses) =>
        Enumerable.Range(0, wins + losses)
            .Select(k => new PlayedGame((k / 2) + 1, k % 2, k < wins ? Results.Win : Results.Loss, null))
            .ToList();

    [Fact]
    public async Task A_duel_already_decided_plays_no_more_games()
    {
        var outcome = await ResumeAsync(Games(100, 150));

        Assert.Equal((100, 150, SprtDecision.NotBetter), (outcome.Wins, outcome.Losses, outcome.Decision));
    }

    [Fact]
    public async Task A_duel_stopped_in_its_second_round_is_judged_with_the_lower_bar()
    {
        // 800 / 700: the first round's test still hesitates, the second round's test keeps it.
        Assert.Equal(SprtDecision.Continue, new Sprt(0, 40, 0.05, 0.05).Decide(800, 700));
        Assert.Equal(SprtDecision.Better, new Sprt(0, 20, 0.05, 0.05).Decide(800, 700));

        var outcome = await ResumeAsync(Games(800, 700));

        Assert.Equal((800, 700, SprtDecision.Better), (outcome.Wins, outcome.Losses, outcome.Decision));
    }

    [Fact]
    public async Task A_journaled_fault_ends_the_duel()
    {
        var played = Games(10, 10);
        played.Add(new PlayedGame(11, 0, Results.Loss, "joueur 0 : pas de réponse à temps"));

        var outcome = await ResumeAsync(played);

        Assert.Equal("joueur 0 : pas de réponse à temps", outcome.CandidateFault);
    }

    [Fact]
    public void Only_the_latest_open_trial_with_a_candidate_against_the_champion_is_resumed()
    {
        var file = Path.Combine(Path.GetTempPath(), $"resume-{Guid.NewGuid():N}.db");
        try
        {
            using var journal = new Journal(file);
            journal.SetChampion("champ", null);
            var older = journal.StartTrial("champ", "p", "c");
            journal.SetTrialCandidate(older, "cand-1", 0);
            var other = journal.StartTrial("former-champ", "p", "c");
            journal.SetTrialCandidate(other, "cand-2", 0);
            var latest = journal.StartTrial("champ", "p", "c");
            journal.SetTrialCandidate(latest, "cand-3", 0);
            journal.AddLocalGame(latest, 1, 0, Results.Win, null);
            journal.AddLocalGame(latest, 1, 1, Results.Loss, null);
            journal.StartTrial("champ", "p", "c");   // still with the thinker: nothing to resume

            Assert.Equal(latest, journal.ResumableTrial());
            Assert.Equal("former-champ", journal.TrialChampion(other));
            Assert.Equal([new PlayedGame(1, 0, Results.Win, null), new PlayedGame(1, 1, Results.Loss, null)], journal.LocalGames(latest));
            Assert.Equal(3, journal.CloseInterruptedTrials(except: latest));
            Assert.Equal(latest, journal.ResumableTrial());
            Assert.Equal(1, journal.CloseInterruptedTrials());
            Assert.Null(journal.ResumableTrial());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }
}
