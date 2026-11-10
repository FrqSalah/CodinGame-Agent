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
        var settings = new CodinGame.Core.Config.ArenaSettings(Referee!, 2, 1000, 75, 3.0, 10, 40, 0, 40, 0.05, 0.05);

        var outcome = await new Duel(new RefereeGame(Referee!), settings, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
            .RunAsync(exe, exe, null, TestContext.Current.CancellationToken);

        Assert.Null(outcome.CandidateFault);
        Assert.NotEqual(CodinGame.Core.Judging.SprtDecision.Better, outcome.Decision);
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
