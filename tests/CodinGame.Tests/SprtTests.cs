using CodinGame.Core.Judging;

namespace CodinGame.Tests;

public class SprtTests
{
    private static readonly Sprt Test = new(elo0: 0, elo1: 40, alpha: 0.05, beta: 0.05);

    [Fact]
    public void Bounds_follow_wald()
    {
        Assert.Equal(Math.Log(0.05 / 0.95), Test.LowerBound, 9);
        Assert.Equal(Math.Log(0.95 / 0.05), Test.UpperBound, 9);
    }

    [Fact]
    public void Expected_score_of_equal_strength_is_one_half() => Assert.Equal(0.5, Sprt.Expected(0), 12);

    [Fact]
    public void Clear_dominance_is_accepted() => Assert.Equal(SprtDecision.Better, Test.Decide(wins: 40, losses: 5));

    [Fact]
    public void Clear_inferiority_is_rejected() => Assert.Equal(SprtDecision.NotBetter, Test.Decide(wins: 5, losses: 40));

    [Fact]
    public void Balanced_results_keep_testing() => Assert.Equal(SprtDecision.Continue, Test.Decide(wins: 10, losses: 10));

    [Fact]
    public void Llr_matches_hand_computation()
    {
        var p1 = 1 / (1 + Math.Pow(10, -40 / 400.0));
        var expected = (12 * Math.Log(p1 / 0.5)) + (7 * Math.Log((1 - p1) / 0.5));
        Assert.Equal(expected, Test.Llr(12, 7), 12);
    }
}
