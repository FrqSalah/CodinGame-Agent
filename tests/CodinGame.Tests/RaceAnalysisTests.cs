using CodinGame.Core.Analysis;
using CodinGame.Core.Arena;
using CodinGame.Core.CodinGameApi;
using CodinGame.Core.Config;
using CodinGame.Core.Data;

namespace CodinGame.Tests;

public class RaceAnalysisTests
{
    private static readonly AnalysisSettings Settings = new(25, 3, 0.5, 1, 30);

    private static readonly PodCommand Idle = new(null, null, 0);

    /// <summary>The engine's move for a pod that hits nothing (ENGINE.md), to build test races.</summary>
    private static PodState Move(PodState p, PodCommand c, bool first = false)
    {
        double a = p.Angle;
        if (c.Tx is { } tx && c.Ty is { } ty)
        {
            var want = Math.Atan2(ty - p.Y, tx - p.X) * 180 / Math.PI;
            a = first ? want : p.Angle + Math.Clamp(((want - p.Angle + 540) % 360) - 180, -18, 18);
        }

        var vx = p.Vx + Math.Cos(a * Math.PI / 180) * c.Thrust;
        var vy = p.Vy + Math.Sin(a * Math.PI / 180) * c.Thrust;
        return new PodState((int)Math.Round(p.X + vx, MidpointRounding.AwayFromZero), (int)Math.Round(p.Y + vy, MidpointRounding.AwayFromZero), (int)(vx * 0.85), (int)(vy * 0.85),
            (int)Math.Round((a % 360 + 360) % 360), p.Next);
    }

    [Fact]
    public void A_pod_that_hits_nothing_follows_its_prediction()
    {
        var p = new PodState(1000, 1000, 0, 0, 0, 1);
        var c = new PodCommand(5000, 1000, 100);

        Assert.Equal(0, RaceAnalysis.Deviation(p, c, new PodState(1100, 1000, 85, 0, 0, 1)));
        Assert.True(RaceAnalysis.Deviation(p, c, new PodState(1100, 1000, -200, 0, 0, 1)) > 100);
    }

    [Fact]
    public void Rotation_is_limited_to_18_degrees_except_on_the_first_turn()
    {
        var target = new PodCommand(1000, 5000, 100);   // straight down: 90°
        var turning = new PodState(1000, 1000, 0, 0, 0, 1);
        var negative = turning with { Angle = -40 };    // the local engine's angles can be negative

        Assert.Equal(0, RaceAnalysis.Deviation(turning, target, Move(turning, target)));
        Assert.Equal(0, RaceAnalysis.Deviation(turning with { Angle = -1 }, target, Move(turning with { Angle = -1 }, target, first: true), first: true));
        Assert.Equal(1000, Move(turning with { Angle = -1 }, target, first: true).X);   // faces the target at once
        Assert.NotEqual(1000, Move(turning, target).X);                                  // still turning
        Assert.True(RaceAnalysis.Deviation(negative, target, Move(negative, target, first: true)) > 25);
    }

    /// <summary>
    /// A 30-turn race. Our runner (pod 0) passes checkpoints after 5, 15 and 25 turns, their runner
    /// (pod 2) every 6 turns. At turn 10, their blocker (pod 3) hits our runner.
    /// </summary>
    private static List<RaceTurn> Race()
    {
        var pods = new[]
        {
            new PodState(1000, 1000, 0, 0, 0, 1),
            new PodState(1000, 4000, 0, 0, 0, 1),
            new PodState(8000, 1000, 0, 0, 0, 1),
            new PodState(1600, 1000, 0, 0, 180, 1),
        };
        var turns = new List<RaceTurn>();
        for (var t = 0; t < 30; t++)
        {
            var commands = new[] { Idle, Idle, Idle, Idle };
            turns.Add(new RaceTurn(pods.ToArray(), commands));
            var next = pods.Select((p, k) => Move(p, commands[k])).ToArray();
            if (t == 10)
            {
                next[0] = next[0] with { Vx = -300 };
                next[3] = next[3] with { Vx = 300 };
            }

            if (t + 1 is 5 or 15 or 25) next[0] = next[0] with { Next = next[0].Next + 1 };
            if ((t + 1) % 6 == 0) next[2] = next[2] with { Next = next[2].Next + 1 };
            pods = next;
        }

        return turns;
    }

    [Fact]
    public void Measures_collisions_pace_and_progress()
    {
        var m = RaceAnalysis.Measure(Race(), Settings.SeuilChoc);

        Assert.Equal(1, m.Us.RunnerHits);
        Assert.Equal(0, m.Them.RunnerHits);
        Assert.Equal(0, m.Us.OwnHits);
        Assert.Equal(3, m.Us.Progress);
        Assert.Equal(4, m.Them.Progress);
        Assert.Equal(7.5, m.Us.Pace!.Value);    // free stretches of 5 and 10 turns; the hit one is left out
        Assert.Equal(6, m.Them.Pace!.Value);
        Assert.Equal(1, m.Gap);
        Assert.Equal(m.Us, m.Swap().Them);
    }

    [Theory]
    [InlineData(true, 5, 5, 8.0, 8.0, 3, "fault")]
    [InlineData(false, 9, 5, 9.0, 8.0, 3, "blocking")]
    [InlineData(false, 6, 5, 9.0, 8.0, 3, "speed")]
    [InlineData(false, 6, 5, 8.2, 8.0, 1, "finish")]
    [InlineData(false, 6, 5, 8.2, 8.0, 4, "other")]
    public void A_lost_game_gets_the_first_cause_that_applies(bool fault, int ourHits, int theirHits, double ourPace, double theirPace, int gap, string cause)
    {
        var m = new RaceMeasures(300, new SideMeasures(ourPace, ourHits, 0, 10), new SideMeasures(theirPace, theirHits, 0, 10 + gap));

        Assert.Equal(cause, RaceAnalysis.Cause(m, fault, Settings));
    }

    [Fact]
    public void Reads_the_trace_line()
    {
        Assert.True(RaceAnalysis.TryParseTrace(RaceAnalysis.ProbeTrace, out var pods));
        Assert.Equal(new PodState(3000, 4500, 0, 0, -1, 1), pods[3]);
        Assert.False(RaceAnalysis.TryParseTrace("T 1 2 3", out _));
        Assert.False(RaceAnalysis.TryParseTrace("sims 12000", out _));
    }

    private static GameFrame Frame(int n, int agent, string referee, string? stderr = null) => new(n, agent, null, stderr, referee, null, null);

    private static string Trace(int x) => $"T {x} 1000 0 0 0 1 {x} 2000 0 0 0 1 {x} 3000 0 0 0 1 {x} 4000 0 0 0 1";

    [Fact]
    public void Reads_a_CodinGame_game_from_our_seat()
    {
        // We are agent 1 (a ranked game); CodinGame merged the standard error of two turns in frame 4.
        var log = new GameLog(1, "", [
            Frame(1, 0, "Pod 1 of player $0 moves towards (100, 200) at power 50\nPod 2 of player $0 boosts towards (300, 400)"),
            Frame(2, 1, "Pod 1 of player $1 enabled its shield\nPod 2 of player $1 moves towards (5, 6) at power 200", Trace(1)),
            Frame(3, 0, "Pod 1 of player $0 waits for its engine to reload\nPod 2 of player $0 moves towards (7, 8) at power 0"),
            Frame(4, 1, "Pod 1 of player $1 moves towards (9, 9) at power 100\nPod 2 of player $1 moves towards (9, 9) at power 100", Trace(2) + "\n" + Trace(3)),
        ], [1, 0], [1, 2], null, Me: 1);

        var turns = RaceAnalysis.FromGameLog(log)!;

        Assert.Equal(2, turns.Count);   // three trace lines, commands for two turns
        Assert.Equal(new PodCommand(null, null, 0, Shield: true), turns[0].Commands[0]);   // our pod 1: shield
        Assert.Equal(new PodCommand(5, 6, 200), turns[0].Commands[1]);
        Assert.Equal(new PodCommand(100, 200, 50), turns[0].Commands[2]);
        Assert.Equal(new PodCommand(300, 400, 650), turns[0].Commands[3]);   // their boost
        Assert.Equal(2, turns[1].Pods[0].X);
        Assert.Null(RaceAnalysis.FromGameLog(log with { Frames = log.Frames.Select(f => f with { Stderr = null }).ToList() }));
    }

    [Fact]
    public void Reads_a_local_game_with_one_boost_per_team_and_the_shield_reload()
    {
        string[] pods = ["1000 1000 0 0 -1 1", "1000 2000 0 0 -1 1", "1000 3000 0 0 -1 1", "1000 4000 0 0 -1 1"];
        var exchanges = new List<RelayExchange>
        {
            new(0, ["3", "2", "5000 5000", "9000 1000", .. pods], ["5000 5000 BOOST", "5000 5000 SHIELD"]),
            new(1, [], ["5000 5000 100", "5000 5000 100"]),
        };
        for (var t = 1; t < 5; t++)
        {
            exchanges.Add(new(0, pods, ["5000 5000 BOOST", "5000 5000 200"]));
            exchanges.Add(new(1, [], ["5000 5000 100 hello", "5000 5000 100"]));
        }

        var turns = RaceAnalysis.FromRelay(exchanges)!;

        Assert.Equal(5, turns.Count);
        Assert.Equal(650, turns[0].Commands[0].Thrust);
        Assert.Equal(200, turns[1].Commands[0].Thrust);   // the team's boost is spent
        Assert.Equal(0, turns[1].Commands[1].Thrust);     // engine reloading after the shield...
        Assert.Equal(0, turns[3].Commands[1].Thrust);
        Assert.Equal(200, turns[4].Commands[1].Thrust);   // ...for three turns
        Assert.Equal(100, turns[1].Commands[2].Thrust);
        Assert.True(turns[0].Commands[1].Shield);
        Assert.False(turns[1].Commands[1].Shield);
    }

    [Fact]
    public void Tells_the_agents_and_the_page_why_the_champion_loses()
    {
        GameAnalysis Game(string result, string? cause, int ourHits) => new("battle", Guid.NewGuid().ToString("N")[..6], null, "h", null, result, false,
            new RaceMeasures(300, new SideMeasures(8.5, ourHits, 1, 12), new SideMeasures(8.0, 2, 0, 15)), cause);
        List<GameAnalysis> games = [Game("Win", null, 2), Game("Loss", "blocking", 9), Game("Loss", "blocking", 7), Game("Loss", "speed", 3)];

        var b = LossReport.Breakdown(games);
        var text = LossReport.ForAgents(games, Settings);

        Assert.Equal((4, 1, 3), (b.Games, b.Won, b.Lost));
        Assert.Equal(("blocking", 2), b.Causes[0]);
        Assert.Contains("4 real CodinGame games", text);
        Assert.Contains("| blocking: our runner was hit clearly more often than theirs | 2 | 67 % |", text);
        Assert.Contains("our runner hit 9 times, theirs 2", text);
        Assert.Contains("the champion does not write the trace line yet", LossReport.ForAgents([], Settings));
        Assert.Equal("runner 8.5 vs 8.0 turns per checkpoint, runner hit 8.0 vs 2.0 times, own pods collided 1.0 vs 0.0 times (2 games)",
            LossReport.Comparison(games.Skip(1).Take(2).Select(g => g.Measures!).ToList()));
    }

    [Fact]
    public void Journals_analyses_and_reads_them_back()
    {
        var file = Path.Combine(Path.GetTempPath(), $"analyses-{Guid.NewGuid():N}.db");
        try
        {
            using var journal = new Journal(file);
            var m = new RaceMeasures(250, new SideMeasures(8.5, 4, 1, 12), new SideMeasures(null, 2, 0, 15));
            journal.AddAnalysis(new GameAnalysis("battle", "42", null, "champ", null, "Loss", false, m, "speed"));
            journal.AddAnalysis(new GameAnalysis("battle", "43", null, "champ", null, "Win", false, null, null));   // no trace

            var read = Assert.Single(journal.Analyses("champ", "battle", "test"));
            Assert.Equal(m, read.Measures);
            Assert.Equal("speed", read.Cause);
            Assert.Equal(new[] { "42", "43" }, journal.AnalysedRefs("battle").Order());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }
}
