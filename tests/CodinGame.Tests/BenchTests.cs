using CodinGame.Core.Arena;
using CodinGame.Core.Data;
using CodinGame.Core.Pipeline;

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

    private static ScoredChampion C(long id, string hash, double score, bool retired = false, long? trial = null) =>
        new(id, hash, trial ?? id + 200, score, 100, retired, new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc).AddHours(id));

    [Fact]
    public void The_bench_takes_the_former_champions_with_the_best_real_scores_never_the_current_one()
    {
        // 2026-11-25: 245 is the champion; 225, 213 and 209 have the best real scores of the others.
        var scored = new[]
        {
            C(1, "h209", 41.42, trial: 209), C(2, "h213", 41.65, trial: 213), C(3, "h225", 41.77, trial: 225),
            C(4, "h237", 41.00, trial: 237), C(5, "h240", 41.41, trial: 240), C(6, "h245", 41.16, trial: 245),
        };

        var pool = PipelineContext.FormerChampions(scored, "h245", 3);

        Assert.Equal(["champion-225", "champion-213", "champion-209"], pool.Select(PipelineContext.FormerChampionName));
        Assert.Empty(PipelineContext.FormerChampions(scored, "h245", 0));
    }

    [Fact]
    public void A_withdrawn_bot_never_joins_the_bench_and_a_restored_one_counts_once_with_its_latest_score()
    {
        // b was withdrawn; a was restored later and scored lower the second time.
        var scored = new[] { C(1, "a", 42.0), C(2, "b", 41.5, retired: true), C(3, "a", 40.0), C(4, "c", 41.0), C(5, "d", 39.0) };

        var pool = PipelineContext.FormerChampions(scored, "d", 3);

        Assert.Equal([("c", 41.0), ("a", 40.0)], pool.Select(c => (c.Hash, c.Score)));
    }
}
