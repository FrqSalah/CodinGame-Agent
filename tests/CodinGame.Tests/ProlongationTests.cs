using CodinGame.Core.Arena;
using CodinGame.Core.Config;
using CodinGame.Core.Judging;

namespace CodinGame.Tests;

/// <summary>The second round: a duel at 600 games without a verdict, with a candidate that leads, goes on with a lower bar.</summary>
public class ProlongationTests
{
    private static readonly ArenaSettings Settings =
        new("csbref", 2, 1000, 75, 3.0, 20, 600, 0, 40, 0.05, 0.05, PartiesProlongation: 3000, LlrProlongation: 0, Elo1Prolongation: 20);

    private static readonly Sprt Second = new(0, 20, 0.05, 0.05);

    [Fact]
    public void A_candidate_that_leads_like_trial_191_gets_a_second_round() =>
        Assert.True(Duel.ShouldProlong(Settings, 600, Second.Llr(316, 284)));

    [Fact]
    public void A_near_tie_like_trial_179_does_not() =>
        Assert.False(Duel.ShouldProlong(Settings, 600, Second.Llr(307, 293)));

    [Fact]
    public void There_is_only_one_second_round() =>
        Assert.False(Duel.ShouldProlong(Settings, 3000, Second.Llr(1600, 1400)));

    [Fact]
    public void The_second_round_can_be_disabled() =>
        Assert.False(Duel.ShouldProlong(Settings with { PartiesProlongation = 600 }, 600, Second.Llr(316, 284)));

    [Fact]
    public void The_lower_bar_keeps_53_percent_and_drops_a_tie_over_2000_games()
    {
        Assert.Equal(SprtDecision.Better, Second.Decide(1070, 930));
        Assert.Equal(SprtDecision.NotBetter, Second.Decide(1000, 1000));
        Assert.Equal(SprtDecision.Continue, new Sprt(0, 40, 0.05, 0.05).Decide(316, 284));   // what the first round said
    }
}
