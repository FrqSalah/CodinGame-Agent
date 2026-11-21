using CodinGame.Core.Arena;

namespace CodinGame.Tests;

/// <summary>
/// Parties réelles : le moteur (csbref) et le bot de départ compilé par g++. Ces tests
/// tournent là où les deux sont installés (CI, conteneur) et sont ignorés ailleurs.
/// </summary>
public class RefereeGameTests
{
    private static readonly string? Referee = FindOnPath("csbref");
    private static readonly string? Compiler = FindOnPath("g++");

    [Fact]
    public async Task Seed_bot_plays_a_full_game_against_itself()
    {
        var exe = await BuildSeedBotAsync();
        var game = new RefereeGame(Referee!);
        await using var a = BotProcess.Start(exe);
        await using var b = BotProcess.Start(exe);

        var result = await game.PlayAsync([a, b], seed: 1, Limits, TestContext.Current.CancellationToken);

        Assert.All(result.Faults, f => Assert.Equal(FaultKind.None, f));
        Assert.InRange(result.Winner, -1, 1);
    }

    [Fact]
    public async Task A_silent_bot_loses_by_timeout()
    {
        var exe = await BuildSeedBotAsync();
        var game = new RefereeGame(Referee!);
        await using var good = BotProcess.Start(exe);
        var silent = new SilentBot();

        var result = await game.PlayAsync([silent, good], seed: 2, Limits, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Winner);
        Assert.Equal(FaultKind.Timeout, result.Faults[0]);
    }

    [Fact]
    public async Task Duel_of_identical_bots_is_not_a_promotion()
    {
        var exe = await BuildSeedBotAsync();
        var settings = new CodinGame.Core.Config.ArenaSettings(Referee!, 2, 1000, 75, 3.0, 10, 40, 0, 40, 0.05, 0.05, 40, 0, 20);

        var outcome = await new Duel(new RefereeGame(Referee!), settings, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
            .RunAsync(exe, exe, null, TestContext.Current.CancellationToken);

        Assert.Null(outcome.CandidateFault);
        Assert.NotEqual(CodinGame.Core.Judging.SprtDecision.Better, outcome.Decision);
    }

    /// <summary>
    /// The loss analysis predicts each pod's move when it hits nothing. On real engine games, every
    /// pod is either on its prediction (rounding) or clearly off it (a collision): nothing between.
    /// </summary>
    [Fact]
    public async Task Collisions_stand_out_clearly_in_engine_games()
    {
        var exe = await BuildSeedBotAsync();
        var game = new RefereeGame(Referee!);
        var deviations = new List<int>();
        var unclear = new List<string>();
        var collisions = 0;
        for (var seed = 1; seed <= 4; seed++)
        {
            await using var a = BotProcess.Start(exe);
            await using var b = BotProcess.Start(exe);
            var result = await game.PlayAsync([a, b], seed, Limits, TestContext.Current.CancellationToken, relay: true);
            var turns = CodinGame.Core.Analysis.RaceAnalysis.FromRelay(result.Relay!)!;
            for (var t = 0; t + 1 < turns.Count; t++)
            {
                for (var k = 0; k < 4; k++)
                {
                    // A shielded pod is ten times heavier: when hit, it barely moves (its partner shows the collision).
                    if (turns[t].Commands[k].Shield)
                    {
                        continue;
                    }

                    var d = CodinGame.Core.Analysis.RaceAnalysis.Deviation(turns[t].Pods[k], turns[t].Commands[k], turns[t + 1].Pods[k], t == 0);
                    deviations.Add(d);
                    if (d is > 5 and <= 25)
                    {
                        unclear.Add($"seed {seed} turn {t} pod {k} dev {d}: {turns[t].Pods[k]} {turns[t].Commands[k]} -> {turns[t + 1].Pods[k]}");
                    }
                }
            }

            var m = CodinGame.Core.Analysis.RaceAnalysis.Measure(turns, 25);
            collisions += m.Us.RunnerHits + m.Them.RunnerHits + m.Us.OwnHits + m.Them.OwnHits;
        }

        Assert.True(deviations.Count > 1000);
        Assert.True(unclear.Count <= deviations.Count / 200,
            $"deviations between 5 and 25: {unclear.Count} of {deviations.Count}\n{string.Join('\n', unclear.Take(12))}");
        Assert.True(collisions > 0);
    }

    /// <summary>
    /// A CodinGame game log holds the map and every command of both players, not the pods: the
    /// analysis replays it on the engine. Here a local game is turned into such a log and replayed,
    /// from either seat: the replay must give back every pod of every turn.
    /// </summary>
    [Fact]
    public async Task A_game_log_replayed_on_the_engine_gives_back_every_pod()
    {
        var exe = await BuildSeedBotAsync();
        var game = new RefereeGame(Referee!);
        await using var a = BotProcess.Start(exe);
        await using var b = BotProcess.Start(exe);
        var result = await game.PlayAsync([a, b], seed: 3, Limits, TestContext.Current.CancellationToken, relay: true);
        var played = CodinGame.Core.Analysis.RaceAnalysis.FromRelay(result.Relay!)!;

        var setup = result.Relay![0].Input;
        var cps = int.Parse(setup[1], System.Globalization.CultureInfo.InvariantCulture);
        var options = "seed=3\npod_timeout=100\nmap=" + string.Join(' ', setup.Skip(2).Take(cps)) + "\npod_per_player=2";
        var frames = result.Relay!.Select((e, i) => new CodinGame.Core.CodinGameApi.GameFrame(i + 1, e.Player, string.Join('\n', e.Output), null, null, null, null)).ToList();
        var log = new CodinGame.Core.CodinGameApi.GameLog(1, options, frames, [0, 1], [1, 2], null);

        var ours = await CodinGame.Core.Analysis.RaceAnalysis.ReplayAsync(game, log, Limits, TestContext.Current.CancellationToken);
        var theirs = await CodinGame.Core.Analysis.RaceAnalysis.ReplayAsync(game, log with { Me = 1 }, Limits, TestContext.Current.CancellationToken);

        Assert.True(played.Count > 50);
        Assert.Equal(played.Count, ours!.Count);
        for (var t = 0; t < played.Count; t++)
        {
            Assert.Equal(played[t].Pods, ours[t].Pods);
            Assert.Equal(played[t].Commands, ours[t].Commands);
            Assert.Equal([played[t].Pods[2], played[t].Pods[3], played[t].Pods[0], played[t].Pods[1]], theirs![t].Pods);
        }
    }

    private static readonly TurnLimits Limits = new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(300));

    private static async Task<string> BuildSeedBotAsync()
    {
        Assert.SkipWhen(Referee is null || Compiler is null, "csbref ou g++ absent : test réservé à la CI et au conteneur");
        var source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "pipelines", "mad-pod-racing", "bot", "Bot.cpp"));
        var cache = Path.Combine(Path.GetTempPath(), "farm-tests-bots");
        var build = await new BotBuilder(cache, "g++ -std=c++20 -O2 -o {sortie} {source}", "Bot.cpp").BuildAsync(source, CancellationToken.None);
        Assert.True(build.Success, build.Errors);
        return build.Executable!;
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, name))
            .FirstOrDefault(File.Exists);

    private sealed class SilentBot : IBotConnection
    {
        public Task SendAsync(IReadOnlyList<string> lines, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
