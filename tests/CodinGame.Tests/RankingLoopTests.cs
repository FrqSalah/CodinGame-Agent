using CodinGame.Core.Arena;
using CodinGame.Core.Data;
using CodinGame.Core.Judging;
using CodinGame.Core.Pipeline;

namespace CodinGame.Tests;

public class RankingLoopTests
{
    private static ScoredChampion C(long id, string hash, double score, bool retired = false) => new(id, hash, id + 100, score, 100, retired);

    [Fact]
    public void A_champion_clearly_below_the_previous_one_is_withdrawn()
    {
        var r = RankingCheck.Regression([C(1, "a", 40.3), C(2, "b", 38.9)], currentHash: "b", tolerance: 1.0);

        Assert.NotNull(r);
        Assert.Equal("b", r.Value.Worse.Hash);
        Assert.Equal("a", r.Value.Restore.Hash);
    }

    [Theory]
    [InlineData(39.5, "b", 1.0)] // within the tolerance: noise
    [InlineData(38.0, "c", 1.0)] // a newer champion already replaced it
    [InlineData(38.0, "b", 0.0)] // rule disabled
    public void No_rollback(double score, string current, double tolerance) =>
        Assert.Null(RankingCheck.Regression([C(1, "a", 40.3), C(2, "b", score)], current, tolerance));

    [Fact]
    public void A_withdrawn_champion_is_never_compared_again()
    {
        // b was withdrawn, a came back and was scored again: no new rollback against b.
        var scored = new[] { C(1, "a", 40.3), C(2, "b", 38.0, retired: true), C(3, "a", 40.1) };

        Assert.Null(RankingCheck.Regression(scored, "a", 1.0));
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
