using CodinGame.Core.Data;
using CodinGame.Core.Pipeline;

namespace CodinGame.Tests;

public class ConfirmationTests
{
    private static readonly IReadOnlyList<(string AgentId, int Rank)> Players =
        Enumerable.Range(1, 200).Select(r => ($"a{r}", r)).ToList();

    [Fact]
    public void Opponents_are_spread_over_the_ranks_just_above_ours()
    {
        var picked = Confirmation.PickOpponents(Players, myRank: 109, count: 6, span: 60);

        Assert.Equal(["a49", "a60", "a72", "a84", "a96", "a108"], picked);
    }

    [Fact]
    public void Near_the_top_the_closest_players_below_complete_the_list()
    {
        var picked = Confirmation.PickOpponents(Players, myRank: 3, count: 4, span: 60);

        Assert.Equal(["a1", "a2", "a4", "a5"], picked);
    }

    [Fact]
    public void Our_own_rank_is_never_picked()
    {
        Assert.DoesNotContain("a109", Confirmation.PickOpponents(Players, 109, 6, 60));
    }

    [Fact]
    public void Games_are_compared_on_the_same_circuits_one_by_one()
    {
        static ApiGameEntry G(string outcome) => new(0, "h", "a1", "seed=1", outcome, false);
        var champion = new[] { G("Win"), G("Win"), G("Loss"), G("Loss") };
        var candidate = new[] { G("Win"), G("Loss"), G("Win"), G("Win") };

        var (plus, minus, cw, chw) = Confirmation.Compare(champion, candidate);

        Assert.Equal((2, 1, 3, 2), (plus, minus, cw, chw));
    }
}

public class TestGameQuotaTests
{
    private static readonly (string, int, int)[] Fresh =
        [("test_games_1m", 10, 10), ("test_games_5m", 20, 20), ("test_games_1h", 80, 80), ("test_games_24h", 600, 600)];

    [Fact]
    public void Short_windows_smaller_than_the_need_do_not_block()
    {
        // 48 games never fit in a 10-per-minute window: they are paced, not refused.
        Assert.Null(CodinGame.Core.CodinGameApi.CodinGameClient.TestGameShortfall(Fresh, 48));
    }

    [Fact]
    public void A_large_window_without_enough_games_blocks()
    {
        var used = new[] { ("test_games_1m", 10, 10), ("test_games_1h", 80, 30), ("test_games_24h", 600, 500) };

        Assert.Equal("30 test games left in window test_games_1h, 48 needed",
            CodinGame.Core.CodinGameApi.CodinGameClient.TestGameShortfall(used, 48));
    }
}
