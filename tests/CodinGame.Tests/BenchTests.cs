using CodinGame.Core.Arena;

namespace CodinGame.Tests;

public class BenchTests
{
    [Fact]
    public void Each_circuit_is_played_from_both_seats_against_each_opponent()
    {
        var cases = Bench.Cases(["a", "b"], 4);

        Assert.Equal(8, cases.Count);
        Assert.Contains(new BenchCase("a", 1, 0), cases);
        Assert.Contains(new BenchCase("a", 1, 1), cases);
        Assert.Contains(new BenchCase("b", 2, 1), cases);
    }

    [Fact]
    public void Comparison_counts_situations_won_by_one_and_lost_by_the_other()
    {
        var cases = Bench.Cases(["a"], 4);
        var candidate = new Dictionary<BenchCase, string>
        {
            [cases[0]] = Results.Win, [cases[1]] = Results.Win, [cases[2]] = Results.Loss, [cases[3]] = Results.Win,
        };
        var champion = new Dictionary<BenchCase, string>
        {
            [cases[0]] = Results.Win, [cases[1]] = Results.Loss, [cases[2]] = Results.Win, [cases[3]] = Results.Loss,
        };

        var (plus, minus, cw, chw) = Bench.Compare(cases, candidate, champion);

        Assert.Equal((2, 1, 3, 2), (plus, minus, cw, chw));
    }
}
