using CodinGame.Core.Arena;
using CodinGame.Core.Data;
using CodinGame.Core.Judging;
using CodinGame.Core.Pipeline;

namespace CodinGame.Tests;

public class RankingLoopTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Window = TimeSpan.FromDays(3);

    /// <summary>Champion number <paramref name="id"/>, promoted <paramref name="hours"/> hours after the start (by default, one per hour).</summary>
    private static ScoredChampion C(long id, string hash, double score, bool retired = false, double? hours = null) =>
        new(id, hash, id + 100, score, 100, retired, Start.AddHours(hours ?? id));

    [Fact]
    public void A_champion_clearly_below_the_previous_one_is_withdrawn()
    {
        var r = RankingCheck.Regression([C(1, "a", 40.3), C(2, "b", 38.9)], currentHash: "b", tolerance: 1.0, Window);

        Assert.NotNull(r);
        Assert.Equal("b", r.Value.Worse.Hash);
        Assert.Equal("a", r.Value.Restore.Hash);
    }

    [Theory]
    [InlineData(39.5, "b", 1.0)] // within the tolerance: noise
    [InlineData(38.0, "c", 1.0)] // a newer champion already replaced it
    [InlineData(38.0, "b", 0.0)] // rule disabled
    public void No_rollback(double score, string current, double tolerance) =>
        Assert.Null(RankingCheck.Regression([C(1, "a", 40.3), C(2, "b", score)], current, tolerance, Window));

    [Fact]
    public void A_withdrawn_champion_is_never_compared_again()
    {
        // b was withdrawn, a came back and was scored again: no new rollback against b.
        var scored = new[] { C(1, "a", 40.3), C(2, "b", 38.0, retired: true), C(3, "a", 40.1) };

        Assert.Null(RankingCheck.Regression(scored, "a", 1.0, Window));
    }

    [Fact]
    public void A_slow_slide_brings_back_the_best_recent_champion()
    {
        // 2026-11-25: each champion within one point of the one before, the last one 1.13 below the best.
        var scored = new[] { C(1, "a", 41.77), C(2, "b", 41.00), C(3, "c", 41.32), C(4, "d", 41.41), C(5, "e", 40.64) };

        var r = RankingCheck.Regression(scored, "e", 1.0, Window);

        Assert.NotNull(r);
        Assert.Equal("e", r.Value.Worse.Hash);
        Assert.Equal("a", r.Value.Restore.Hash);
    }

    [Fact]
    public void A_score_older_than_the_window_is_left_out_but_the_previous_champion_always_counts()
    {
        // a was promoted four days before b, b one week before c.
        var scored = new[] { C(1, "a", 42.0, hours: 0), C(2, "b", 40.0, hours: 96), C(3, "c", 38.5, hours: 264) };

        var r = RankingCheck.Regression(scored, "c", 1.0, Window);

        Assert.NotNull(r);
        Assert.Equal("b", r.Value.Restore.Hash);
        Assert.Equal("b", RankingCheck.Regression(scored, "c", 1.0, TimeSpan.Zero)?.Restore.Hash);
    }

    [Fact]
    public void A_bot_withdrawn_once_is_never_restored_so_two_champions_cannot_take_turns()
    {
        // a came back after d, then scored lower than before: b is restored once, then a no longer counts.
        var first = new[] { C(1, "a", 41.8), C(2, "b", 41.4), C(3, "d", 40.6, retired: true), C(4, "a", 40.2) };
        var r = RankingCheck.Regression(first, "a", 1.0, Window);
        Assert.Equal("b", r?.Restore.Hash);

        var then = new[] { C(1, "a", 41.8), C(2, "b", 41.4), C(3, "d", 40.6, retired: true), C(4, "a", 40.2, retired: true), C(5, "b", 40.3) };
        Assert.Null(RankingCheck.Regression(then, "b", 1.0, Window));
    }

    [Fact]
    public void Lab_report_tells_the_coder_about_a_failure()
    {
        var report = Trial.LabReport(new DuelOutcome(3, 17, 0, SprtDecision.NotBetter, -3, "timeout at turn 12"), 0.15);

        Assert.Contains("3 wins, 17 losses", report);
        Assert.Contains("FAILED", report);
        Assert.Contains("timeout at turn 12", report);
    }
}
