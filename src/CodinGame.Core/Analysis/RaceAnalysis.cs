using System.Globalization;
using CodinGame.Core.Arena;
using CodinGame.Core.CodinGameApi;
using CodinGame.Core.Config;

namespace CodinGame.Core.Analysis;

/// <summary>One pod at the start of a turn, as the game input gives it.</summary>
public readonly record struct PodState(int X, int Y, int Vx, int Vy, int Angle, int Next);

/// <summary>
/// What a pod did this turn: a target and a thrust (650 = BOOST). No target: shield or engine
/// reloading. <paramref name="Shield"/>: the pod raised its shield this turn (ten times heavier: when
/// hit, it barely moves, and its partner in the collision is the one far from its prediction).
/// </summary>
public readonly record struct PodCommand(int? Tx, int? Ty, int Thrust, bool Shield = false);

/// <summary>One turn seen from one player: its pods are 0 and 1, the opponent's 2 and 3.</summary>
public sealed record RaceTurn(PodState[] Pods, PodCommand[] Commands);

/// <summary>
/// One team over a game. <paramref name="Pace"/>: turns per checkpoint of its runner on the
/// stretches where the runner hit nothing (null if none). <paramref name="RunnerHits"/>:
/// collisions of its runner with enemy pods. <paramref name="OwnHits"/>: collisions between its
/// two pods. <paramref name="Progress"/>: checkpoints passed by its best pod.
/// </summary>
public sealed record SideMeasures(double? Pace, int RunnerHits, int OwnHits, int Progress);

public sealed record RaceMeasures(int Turns, SideMeasures Us, SideMeasures Them)
{
    /// <summary>Checkpoints by which the opponent's best pod is ahead of ours at the end.</summary>
    public int Gap => Them.Progress - Us.Progress;

    public RaceMeasures Swap() => this with { Us = Them, Them = Us };
}

/// <summary>Main cause of a lost game, by a fixed rule (<see cref="RaceAnalysis.Cause"/>).</summary>
public static class LossCauses
{
    public const string Fault = "fault";
    public const string Blocking = "blocking";
    public const string Speed = "speed";
    public const string Finish = "finish";
    public const string Other = "other";

    public static readonly string[] All = [Fault, Blocking, Speed, Finish, Other];

    /// <summary>What each cause means, for the agents (English) and for the status page (French).</summary>
    public static string English(string cause) => cause switch
    {
        Fault => "fault: timeout, crash or invalid output",
        Blocking => "blocking: our runner was hit clearly more often than theirs",
        Speed => "speed: our runner was slower on collision-free stretches",
        Finish => "finish: lost by a narrow margin at the end",
        _ => "other: none of the above",
    };

    public static string French(string cause) => cause switch
    {
        Fault => "Faute (temps, plantage, sortie invalide)",
        Blocking => "Bataille de blocage perdue",
        Speed => "Coureur plus lent",
        Finish => "Arrivée serrée",
        _ => "Autre",
    };
}

/// <summary>
/// Measures a Mad Pod Racing game from its turns: positions and speeds of the 4 pods, and the
/// command of each pod. A collision is found without simulating the race: from a pod's state and
/// command, the next state of a pod that hits nothing is fixed (rotation, thrust, rounding,
/// friction, see ENGINE.md); a pod far from that prediction was hit. The rest are counts.
/// </summary>
public static class RaceAnalysis
{
    /// <summary>
    /// Distance between a pod's next state and the no-collision prediction (position or speed).
    /// <paramref name="first"/>: the first turn, where a pod turns freely (told by the turn number:
    /// a pod facing angle -1 is not proof of a first turn).
    /// </summary>
    public static int Deviation(PodState p, PodCommand c, PodState next, bool first = false)
    {
        double a = p.Angle;
        if (c.Tx is { } tx && c.Ty is { } ty)
        {
            var want = Math.Atan2(ty - p.Y, tx - p.X) * 180 / Math.PI;
            a = first ? want : p.Angle + Math.Clamp(((want - p.Angle + 540) % 360) - 180, -18, 18);
        }

        var vx = p.Vx + Math.Cos(a * Math.PI / 180) * c.Thrust;
        var vy = p.Vy + Math.Sin(a * Math.PI / 180) * c.Thrust;
        var x = (int)Math.Round(p.X + vx, MidpointRounding.AwayFromZero);
        var y = (int)Math.Round(p.Y + vy, MidpointRounding.AwayFromZero);
        return new[] { Math.Abs(x - next.X), Math.Abs(y - next.Y), Math.Abs((int)(vx * 0.85) - next.Vx), Math.Abs((int)(vy * 0.85) - next.Vy) }.Max();
    }

    /// <summary>Every deviation of every pod in a game: a clean game has small ones and collisions, nothing between.</summary>
    public static IEnumerable<int> Deviations(IReadOnlyList<RaceTurn> turns) =>
        Enumerable.Range(0, Math.Max(0, turns.Count - 1))
            .SelectMany(t => Enumerable.Range(0, 4).Select(k => Deviation(turns[t].Pods[k], turns[t].Commands[k], turns[t + 1].Pods[k], t == 0)));

    public static RaceMeasures Measure(IReadOnlyList<RaceTurn> turns, double collisionThreshold)
    {
        var progress = new int[4];
        var segmentStart = new int[4];
        var segmentHit = new bool[4];
        var freeSegments = Enumerable.Range(0, 4).Select(_ => new List<int>()).ToArray();
        var runnerHits = new int[2];
        var ownHits = new int[2];

        for (var t = 0; t + 1 < turns.Count; t++)
        {
            var now = turns[t];
            var next = turns[t + 1].Pods;
            var hit = Enumerable.Range(0, 4).Where(k => Deviation(now.Pods[k], now.Commands[k], next[k], t == 0) > collisionThreshold).ToList();

            // Each pod far from its prediction collided with the nearest pod that also deviated, or
            // else with the nearest pod (a shielded pod, ten times heavier, barely moves).
            var pairs = new HashSet<(int, int)>();
            foreach (var k in hit)
            {
                var candidates = hit.Count > 1 ? hit.Where(j => j != k) : Enumerable.Range(0, 4).Where(j => j != k);
                var other = candidates.MinBy(j => Distance(next[k], next[j]));
                pairs.Add((Math.Min(k, other), Math.Max(k, other)));
            }

            foreach (var (a, b) in pairs)
            {
                segmentHit[a] = segmentHit[b] = true;
                if (a / 2 == b / 2)
                {
                    ownHits[a / 2]++;
                    continue;
                }

                if (IsRunner(a, progress)) runnerHits[a / 2]++;
                if (IsRunner(b, progress)) runnerHits[b / 2]++;
            }

            for (var k = 0; k < 4; k++)
            {
                if (next[k].Next == now.Pods[k].Next)
                {
                    continue;
                }

                progress[k]++;
                if (!segmentHit[k])
                {
                    freeSegments[k].Add(t + 1 - segmentStart[k]);
                }

                segmentStart[k] = t + 1;
                segmentHit[k] = false;
            }
        }

        SideMeasures Side(int team)
        {
            var runner = progress[team * 2] >= progress[team * 2 + 1] ? team * 2 : team * 2 + 1;
            var free = freeSegments[runner];
            return new SideMeasures(free.Count == 0 ? null : free.Average(), runnerHits[team], ownHits[team], progress[runner]);
        }

        return new RaceMeasures(turns.Count, Side(0), Side(1));
    }

    /// <summary>The runner of a team is its pod that passed more checkpoints (the first pod on a tie).</summary>
    private static bool IsRunner(int pod, int[] progress)
    {
        var mate = pod ^ 1;
        return progress[pod] > progress[mate] || (progress[pod] == progress[mate] && pod < mate);
    }

    private static double Distance(PodState a, PodState b) => Math.Sqrt(((double)a.X - b.X) * (a.X - b.X) + ((double)a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Main cause of a lost game, the first rule that applies, in this order.</summary>
    public static string Cause(RaceMeasures m, bool fault, AnalysisSettings s)
    {
        if (fault)
        {
            return LossCauses.Fault;
        }

        if (m.Us.RunnerHits - m.Them.RunnerHits >= s.EcartChocs)
        {
            return LossCauses.Blocking;
        }

        if (m.Us.Pace is { } ours && m.Them.Pace is { } theirs && ours - theirs >= s.EcartCadence)
        {
            return LossCauses.Speed;
        }

        return m.Gap <= s.ArriveeSerree ? LossCauses.Finish : LossCauses.Other;
    }

    // ---- Turns of a local game ----

    /// <summary>
    /// Turns of a local game seen from player 0: pods from the lines the engine sent it, commands
    /// from both players' answers. BOOST is one per team (a second one is full thrust), and a
    /// SHIELD stops the engine for the next 3 turns, as in ENGINE.md.
    /// </summary>
    public static IReadOnlyList<RaceTurn>? FromRelay(IReadOnlyList<RelayExchange> exchanges)
    {
        var mine = exchanges.Where(e => e.Player == 0).ToList();
        var theirs = exchanges.Where(e => e.Player == 1).ToList();
        if (mine.Count == 0 || mine[0].Input.Count < 2 || !int.TryParse(mine[0].Input[1], out var cpCount))
        {
            return null;
        }

        var boostUsed = new bool[2];
        var reload = new int[4];
        var turns = new List<RaceTurn>();
        for (var t = 0; t < Math.Min(mine.Count, theirs.Count); t++)
        {
            var input = t == 0 ? mine[t].Input.Skip(2 + cpCount).ToList() : mine[t].Input;
            if (input.Count != 4 || mine[t].Output.Count != 2 || theirs[t].Output.Count != 2)
            {
                break;
            }

            // The engine sends angles from -180 to 180, CodinGame from 0 to 359 (and -1 before the first move).
            var pods = input.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Int).ToArray())
                .Select(v => new PodState(v[0], v[1], v[2], v[3], t == 0 ? v[4] : (v[4] % 360 + 360) % 360, v[5])).ToArray();
            var answers = mine[t].Output.Concat(theirs[t].Output).ToList();
            var commands = new PodCommand[4];
            for (var k = 0; k < 4; k++)
            {
                var w = answers[k].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var (tx, ty) = (Int(w[0]), Int(w[1]));
                var order = w.Length > 2 ? w[2] : "0";
                if (reload[k] > 0)
                {
                    reload[k]--;
                    commands[k] = order == "SHIELD" ? Shield(k) : new PodCommand(null, null, 0);
                }
                else if (order == "SHIELD")
                {
                    commands[k] = Shield(k);
                }
                else if (order == "BOOST")
                {
                    commands[k] = new PodCommand(tx, ty, boostUsed[k / 2] ? 200 : 650);
                    boostUsed[k / 2] = true;
                }
                else
                {
                    commands[k] = new PodCommand(tx, ty, int.TryParse(order, out var p) ? p : 0);
                }
            }

            turns.Add(new RaceTurn(pods, commands));
        }

        return turns.Count < 2 ? null : turns;

        PodCommand Shield(int k)
        {
            reload[k] = 3;
            return new PodCommand(null, null, 0, Shield: true);
        }
    }

    private static int Int(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

    // ---- Turns of a CodinGame game, replayed ----

    /// <summary>The checkpoints of a CodinGame game, from its options (« map=x y x y … »), or null.</summary>
    public static IReadOnlyList<(int X, int Y)>? MapOf(string options)
    {
        var line = options.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("map=", StringComparison.Ordinal));
        var v = line?[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null).ToList();
        return v is null || v.Count < 4 || v.Count % 2 != 0 || v.Any(n => n is null)
            ? null
            : Enumerable.Range(0, v.Count / 2).Select(i => (v[2 * i]!.Value, v[2 * i + 1]!.Value)).ToList();
    }

    /// <summary>
    /// Turns of a CodinGame game seen from our agent, replayed on the local engine: its map, and the
    /// commands both players wrote. The physics is the same, so the engine gives back every pod's
    /// position: no trace is needed, for any bot. Null without a map or turns.
    /// </summary>
    public static async Task<IReadOnlyList<RaceTurn>?> ReplayAsync(RefereeGame engine, GameLog log, TurnLimits limits, CancellationToken ct)
    {
        if (MapOf(log.Options) is not { } map)
        {
            return null;
        }

        IBotConnection[] bots = [new ReplayBot(log.StdoutOf(0)), new ReplayBot(log.StdoutOf(1))];
        var r = await engine.PlayAsync(bots, 0, limits, ct, relay: true, map: map);
        if (r.Relay is null || FromRelay(r.Relay) is not { } turns)
        {
            return null;
        }

        return log.Me == 0 ? turns : turns.Select(t => new RaceTurn(
            [t.Pods[2], t.Pods[3], t.Pods[0], t.Pods[1]], [t.Commands[2], t.Commands[3], t.Commands[0], t.Commands[1]])).ToList();
    }

    /// <summary>A player that writes again, in order, the lines a real bot wrote.</summary>
    private sealed class ReplayBot(IEnumerable<string> lines) : IBotConnection
    {
        private readonly Queue<string> _lines = new(lines.Select(l => l.Trim()).Where(l => l.Length > 0));

        public Task SendAsync(IReadOnlyList<string> lines, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(_lines.TryDequeue(out var l) ? l : null);
    }

    // ---- Analysed games, ready for the journal ----

    /// <summary>
    /// A CodinGame game of ours (<paramref name="source"/>: test or battle), measured by replaying it
    /// on <paramref name="engine"/>. A game that cannot be replayed is kept without measures.
    /// </summary>
    public static async Task<Data.GameAnalysis> OfGameAsync(
        string source, GameLog log, string botHash, string? opponent, long? trialId, AnalysisSettings s, RefereeGame engine, TurnLimits limits, CancellationToken ct)
    {
        var outcome = log.OutcomeFor(log.Me).ToString();
        var fault = source == "test" ? log.TestedCodeFaulted : log.MeFaulted;
        IReadOnlyList<RaceTurn>? turns;
        try
        {
            turns = await ReplayAsync(engine, log, limits, ct);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or IndexOutOfRangeException)
        {
            turns = null;   // an output the engine or the reading does not accept: the game stays unmeasured
        }

        var measures = turns is null ? null : Measure(turns, s.SeuilChoc);
        var cause = measures is not null && outcome == nameof(Outcome.Loss) ? Cause(measures, fault, s) : null;
        return new Data.GameAnalysis(source, log.GameId.ToString(CultureInfo.InvariantCulture), trialId, botHash, opponent, outcome, fault, measures, cause);
    }

    /// <summary>A local duel game, seen from the candidate in <paramref name="seat"/>. Null without its turns.</summary>
    public static Data.GameAnalysis? OfLocalGame(MatchResult r, int seat, long seed, long trialId, string candidateHash, string championHash, AnalysisSettings s)
    {
        if (r.Relay is null || FromRelay(r.Relay) is not { } turns)
        {
            return null;
        }

        var measures = Measure(turns, s.SeuilChoc);
        if (seat == 1)
        {
            measures = measures.Swap();
        }

        var outcome = r.IsDraw ? Outcome.Draw : r.Winner == seat ? Outcome.Win : Outcome.Loss;
        var fault = r.Faults[seat] != FaultKind.None;
        return new Data.GameAnalysis("local", string.Create(CultureInfo.InvariantCulture, $"{trialId}:{seed}:{seat}"), trialId, candidateHash, championHash,
            outcome.ToString(), fault, measures, outcome == Outcome.Loss ? Cause(measures, fault, s) : null);
    }
}
