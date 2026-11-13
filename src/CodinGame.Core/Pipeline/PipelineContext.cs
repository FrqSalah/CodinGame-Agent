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

    /// <summary>Compile les adversaires extérieurs du banc ; renvoie leur nom (fichier sans extension) et leur exécutable.</summary>
    public async Task<Dictionary<string, string>> BuildBenchOpponentsAsync(CancellationToken ct)
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

        return exes;
    }

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
        .Replace("{fichier}", Config.Bot.Fichier);
}
