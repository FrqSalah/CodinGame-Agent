using System.Globalization;
using System.Text;
using CodinGame.Core.Data;

namespace CodinGame.Core.Analysis;

/// <summary>Lost games of a bot, counted by cause.</summary>
public sealed record LossBreakdown(int Games, int Won, int Lost, IReadOnlyList<(string Cause, int Count)> Causes);

/// <summary>What the analysed games tell the agents and the user. Counts only: no judgment.</summary>
public static class LossReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static LossBreakdown Breakdown(IReadOnlyList<GameAnalysis> games)
    {
        var lost = games.Where(g => g.Result == "Loss").ToList();
        var causes = LossCauses.All
            .Select(c => (c, lost.Count(g => (g.Cause ?? LossCauses.Other) == c)))
            .Where(x => x.Item2 > 0)
            .OrderByDescending(x => x.Item2)
            .ToList();
        return new LossBreakdown(games.Count, games.Count(g => g.Result == "Win"), lost.Count, causes);
    }

    /// <summary>
    /// One line for the history and the lab: the candidate (Us) against the champion (Them), as an
    /// average per local game. Null without measured games.
    /// </summary>
    public static string? Comparison(IReadOnlyList<RaceMeasures> games)
    {
        if (games.Count == 0)
        {
            return null;
        }

        var paced = games.Where(g => g.Us.Pace is not null && g.Them.Pace is not null).ToList();
        var pace = paced.Count == 0 ? "" : string.Format(Inv, "runner {0:0.0} vs {1:0.0} turns per checkpoint, ",
            paced.Average(g => g.Us.Pace!.Value), paced.Average(g => g.Them.Pace!.Value));
        return string.Format(Inv, "{0}runner hit {1:0.0} vs {2:0.0} times, own pods collided {3:0.0} vs {4:0.0} times ({5} games)",
            pace, games.Average(g => g.Us.RunnerHits), games.Average(g => g.Them.RunnerHits),
            games.Average(g => g.Us.OwnHits), games.Average(g => g.Them.OwnHits), games.Count);
    }

    /// <summary>LOSSES.md: why the champion loses against real players, for the thinker and the fresh look.</summary>
    public static string ForAgents(IReadOnlyList<GameAnalysis> games, Config.AnalysisSettings s)
    {
        var sb = new StringBuilder("# Why the champion loses against real players\n\n");
        if (games.Count == 0)
        {
            return sb.Append("No real CodinGame game of the current champion has been analysed yet: the farm replays its ")
                .Append("ranked games at every ranking reading.\n").ToString();
        }

        var b = Breakdown(games);
        sb.Append(Inv, $"The farm measured {b.Games} real CodinGame games of the current champion (ranked games and ")
          .Append(Inv, $"confirmation games against players ranked above us): {b.Won} won, {b.Lost} lost. Each lost game ")
          .Append("gets one main cause, by a fixed rule applied in this order:\n\n");
        sb.Append(Inv, $"1. {LossCauses.English(LossCauses.Fault)}.\n");
        sb.Append(Inv, $"2. blocking: our runner was hit by enemy pods at least {s.EcartChocs} times more than their runner by ours.\n");
        sb.Append(Inv, $"3. speed: on stretches where it hit nothing, our runner took at least {s.EcartCadence:0.0#} more turns per checkpoint than theirs.\n");
        sb.Append(Inv, $"4. finish: lost by at most {s.ArriveeSerree} checkpoint(s).\n");
        sb.Append("5. other.\n\n");

        if (b.Lost > 0)
        {
            sb.Append("| Cause | Lost games | Share |\n|---|---|---|\n");
            foreach (var (cause, count) in b.Causes)
            {
                sb.Append(Inv, $"| {LossCauses.English(cause)} | {count} | {(double)count / b.Lost:P0} |\n");
            }
        }

        sb.Append("\n## Averages per game\n\n| Measure | Won games | Lost games |\n|---|---|---|\n");
        var won = games.Where(g => g.Result == "Win").Select(g => g.Measures!).ToList();
        var lostGames = games.Where(g => g.Result == "Loss").Select(g => g.Measures!).ToList();
        Row(sb, "Our runner, turns per checkpoint without collision", won, lostGames, m => m.Us.Pace);
        Row(sb, "Their runner, turns per checkpoint without collision", won, lostGames, m => m.Them.Pace);
        Row(sb, "Hits on our runner", won, lostGames, m => m.Us.RunnerHits);
        Row(sb, "Hits on their runner", won, lostGames, m => m.Them.RunnerHits);
        Row(sb, "Collisions between our own pods", won, lostGames, m => m.Us.OwnHits);
        Row(sb, "Game length in turns", won, lostGames, m => m.Turns);

        var examples = games.Where(g => g.Result == "Loss" && g.Cause is not null and not LossCauses.Other and not LossCauses.Fault)
            .GroupBy(g => g.Cause!)
            .Select(group => group.MaxBy(g => Strength(g.Cause!, g.Measures!))!)
            .ToList();
        if (examples.Count > 0)
        {
            sb.Append("\n## The clearest lost game of each cause\n\n");
            foreach (var g in examples)
            {
                var m = g.Measures!;
                sb.Append(Inv, $"- {g.Cause}: game {g.Ref} ({g.Source}), {m.Turns} turns: our runner hit {m.Us.RunnerHits} times, theirs {m.Them.RunnerHits}; ")
                  .Append(Inv, $"runner pace {Pace(m.Us.Pace)} vs {Pace(m.Them.Pace)} turns per checkpoint; lost by {m.Gap} checkpoint(s).\n");
            }
        }

        return sb.ToString();
    }

    private static double Strength(string cause, RaceMeasures m) => cause switch
    {
        LossCauses.Blocking => m.Us.RunnerHits - m.Them.RunnerHits,
        LossCauses.Speed => (m.Us.Pace ?? 0) - (m.Them.Pace ?? 0),
        _ => -m.Gap,
    };

    private static string Pace(double? p) => p is { } v ? v.ToString("0.0", Inv) : "n/a";

    private static void Row(StringBuilder sb, string name, List<RaceMeasures> won, List<RaceMeasures> lost, Func<RaceMeasures, double?> value)
    {
        static string Avg(List<RaceMeasures> games, Func<RaceMeasures, double?> value)
        {
            var values = games.Select(value).OfType<double>().ToList();
            return values.Count == 0 ? "n/a" : values.Average().ToString("0.0", Inv);
        }

        sb.Append(Inv, $"| {name} | {Avg(won, value)} | {Avg(lost, value)} |\n");
    }
}
