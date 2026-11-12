using CodinGame.Core.Arena;
using CodinGame.Core.Config;
using CodinGame.Core.Judging;

namespace CodinGame.Tests;

public class ProlongationTests
{
    private static readonly ArenaSettings Settings =
        new("csbref", 2, 1000, 75, 3.0, 20, 600, 0, 40, 0.05, 0.05, PartiesProlongation: 1000, LlrProlongation: 1.5);

    private static readonly Sprt Judge = new(0, 40, 0.05, 0.05);

    [Fact]
    public void A_near_miss_like_trial_63_is_prolonged() =>
        Assert.True(Duel.ShouldProlong(Settings, 600, Judge.Llr(328, 272)));

    [Fact]
    public void A_tie_is_not_prolonged() =>
        Assert.False(Duel.ShouldProlong(Settings, 600, Judge.Llr(308, 292)));

    [Fact]
    public void A_duel_is_prolonged_only_once() =>
        Assert.False(Duel.ShouldProlong(Settings, 1000, Judge.Llr(560, 440)));

    [Fact]
    public void Prolongation_can_be_disabled() =>
        Assert.False(Duel.ShouldProlong(Settings with { PartiesProlongation = 600 }, 600, Judge.Llr(328, 272)));
}
