using System.Globalization;
using CodinGame.Core.Arena;
using CodinGame.Core.Config;
using CodinGame.Core.Data;

namespace CodinGame.Core.Pipeline;

/// <summary>Ce qu'un essai a besoin de connaître de sa pipeline.</summary>
public sealed class PipelineContext(string name, PipelinePaths paths, PipelineConfig config, Journal journal)
{
    public string Name { get; } = name;
    public PipelinePaths Paths { get; } = paths;
    public PipelineConfig Config { get; } = config;
    public RefereeGame Referee { get; } = new(config.Arene.Arbitre);
    public Journal Journal { get; } = journal;

    public BotBuilder Builder { get; } = new(paths.BotCache, config.Bot.Compilation, config.Bot.Fichier);

    /// <summary>
    /// Builds the bench opponents and returns their name and executable: the outside bots of
    /// <c>banc.adversaires</c> (named after their file), then the former champions with the best real
    /// scores (<c>banc.anciens_champions</c>, named after their trial), never <paramref name="champion"/>.
    /// </summary>
    public async Task<Dictionary<string, string>> BuildBenchOpponentsAsync(string? champion, CancellationToken ct)
    {
        var exes = new Dictionary<string, string>();
        var builder = new BotBuilder(Paths.BotCache, Config.Banc.Compilation, "Bot.cpp");
        foreach (var path in Config.Banc.Adversaires)
        {
            var build = await builder.BuildAsync(await File.ReadAllTextAsync(path, ct), ct);
            if (!build.Success)
            {
                throw new InvalidOperationException($"L'adversaire extérieur {path} ne compile pas : {build.Errors[..Math.Min(200, build.Errors.Length)]}");
            }

            exes[Path.GetFileNameWithoutExtension(path)] = build.Executable!;
        }

        foreach (var former in FormerChampions(Journal.ScoredChampions(), champion, Config.Banc.AnciensChampions))
        {
            // Built like the champion itself; one that no longer builds only leaves the bench smaller.
            if (Journal.BotSource(former.Hash) is { } source && await Builder.BuildAsync(source, ct) is { Success: true } build)
            {
                exes[FormerChampionName(former)] = build.Executable!;
            }
        }

        return exes;
    }

    /// <summary>
    /// The former champions that join the bench: the <paramref name="count"/> best real scores among
    /// bots never withdrawn, each bot once with its latest score, never <paramref name="current"/>.
    /// Beating only the latest champion is not enough: candidates that just exploit it would pass
    /// (2026-11-25: nine candidates kept on local duels, 40 against 46 against real players).
    /// </summary>
    public static IReadOnlyList<ScoredChampion> FormerChampions(IReadOnlyList<ScoredChampion> scored, string? current, int count)
    {
        var withdrawn = scored.Where(c => c.Retired).Select(c => c.Hash).ToHashSet();
        return scored
            .Where(c => c.Hash != current && !withdrawn.Contains(c.Hash))
            .GroupBy(c => c.Hash)
            .Select(g => g.Last())
            .OrderByDescending(c => c.Score)
            .Take(count)
            .ToList();
    }

    /// <summary>A former champion's name on the bench and in the journal: its trial, or its hash for the earliest ones.</summary>
    public static string FormerChampionName(ScoredChampion c) =>
        $"champion-{(c.TrialId is { } t ? t.ToString(CultureInfo.InvariantCulture) : c.Hash[..Math.Min(8, c.Hash.Length)])}";

    /// <summary>Joue le banc pour un bot (les situations déjà au journal ne sont pas rejouées) et renvoie ses résultats.</summary>
    public async Task<Dictionary<BenchCase, string>> PlayBenchAsync(
        string botHash, string botExe, IReadOnlyDictionary<string, string> opponents, Action<BenchCase, MatchResult>? onFault,
        Microsoft.Extensions.Logging.ILogger log, CancellationToken ct)
    {
        var cases = Bench.Cases(opponents.Keys, Config.Banc.PartiesParAdversaire);
        await new Bench(Referee, Config.Arene, log).PlayAsync(botExe, opponents, cases, Journal.BenchResults(botHash),
            (c, r) =>
            {
                Journal.AddBenchGame(botHash, c, Results.For(r, c.Seat), r.Faults[c.Seat] == FaultKind.None ? null : r.Detail);
                if (r.Faults[c.Seat] != FaultKind.None)
                {
                    onFault?.Invoke(c, r);
                }
            }, ct);
        var all = Journal.BenchResults(botHash);
        return cases.ToDictionary(c => c, c => all[c]);
    }

    /// <summary>Remplace les repères d'un gabarit de consigne par les valeurs de la pipeline.</summary>
    public string Fill(string template) => template
        .Replace("{jeu}", Config.Jeu)
        .Replace("{ligue}", string.Join(" / ", Config.Ligues))
        .Replace("{langage}", Config.Langage)
        .Replace("{fichier}", Config.Bot.Fichier)
        .Replace("{mots_note}", Config.Boucle.MotsNoteMax.ToString(CultureInfo.InvariantCulture));
}
