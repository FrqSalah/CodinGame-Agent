using System.Text.Json;

namespace CodinGame.Core.CodinGameApi;

public sealed record GameFrame(int Frame, int? Agent, string? Stdout, string? Stderr, string? Referee, string? Summary, string? Error);

public enum Outcome
{
    Win,
    Loss,
    Draw,
}

/// <summary>
/// Journal JSON d'une partie CodinGame (<c>format=json</c>). Dans une partie de test,
/// l'agent 0 est toujours le code testé.
/// </summary>
public sealed record GameLog(
    long GameId,
    string Options,
    IReadOnlyList<GameFrame> Frames,
    IReadOnlyList<int> Ranking,
    IReadOnlyList<double> Scores,
    string? FailCause,
    int Me = 0)
{
    public static GameLog Parse(JsonElement log)
    {
        var frames = new List<GameFrame>();
        if (log.TryGetProperty("frames", out var fs))
        {
            foreach (var f in fs.EnumerateArray())
            {
                frames.Add(new GameFrame(
                    f.GetProperty("frame").GetInt32(),
                    f.TryGetProperty("agent", out var a) ? a.GetInt32() : null,
                    Str(f, "stdout"),
                    Str(f, "stderr"),
                    Str(f, "referee"),
                    Str(f, "summary"),
                    f.TryGetProperty("error", out var e) ? e.ToString() : null));
            }
        }

        return new GameLog(
            log.TryGetProperty("game_id", out var id) ? id.GetInt64() : 0,
            Str(log, "game_options") ?? "",
            frames,
            log.TryGetProperty("ranking", out var r) ? r.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [],
            log.TryGetProperty("scores", out var s) ? s.EnumerateArray().Select(x => x.GetDouble()).ToArray() : [],
            Str(log, "fail_cause"),
            MeOf(log));
    }

    /// <summary>Index of our agent: 0 in a test game, either seat in a ranked battle.</summary>
    private static int MeOf(JsonElement log)
    {
        if (log.TryGetProperty("agents", out var agents))
        {
            foreach (var a in agents.EnumerateArray())
            {
                if (a.TryGetProperty("me", out var me) && me.ValueKind == JsonValueKind.True && a.TryGetProperty("index", out var i))
                {
                    return i.GetInt32();
                }
            }
        }

        return 0;
    }

    /// <summary>Did our agent crash or time out? In a battle, only the frames tell (an error, or the referee's timeout line).</summary>
    public bool MeFaulted =>
        Frames.Any(f => (f.Agent == Me && f.Error is not null) || f.Referee?.Contains($"${Me} has not provided", StringComparison.Ordinal) == true);

    /// <summary>Le code testé (agent 0) a-t-il planté, dépassé le temps ou joué un coup invalide ?</summary>
    public bool TestedCodeFaulted =>
        FailCause is not null || Frames.Any(f => f.Agent == 0 && f.Error is not null);

    public Outcome OutcomeFor(int agent)
    {
        if (Scores.Count >= 2 && Scores.Distinct().Count() == 1)
        {
            return Outcome.Draw;
        }

        return Ranking.Count > 0 && Ranking[0] == agent ? Outcome.Win : Outcome.Loss;
    }

    /// <summary>Les lignes écrites par un agent, dans l'ordre des tours.</summary>
    public IEnumerable<string> StdoutOf(int agent) =>
        Frames.Where(f => f.Agent == agent && f.Stdout is not null)
              .SelectMany(f => f.Stdout!.Split('\n'));

    public IEnumerable<string> StderrOf(int agent) =>
        Frames.Where(f => f.Agent == agent && f.Stderr is not null)
              .SelectMany(f => f.Stderr!.Split('\n'));

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
