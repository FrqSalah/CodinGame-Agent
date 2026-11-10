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

    /// <summary>Remplace les repères d'un gabarit de consigne par les valeurs de la pipeline.</summary>
    public string Fill(string template) => template
        .Replace("{jeu}", Config.Jeu)
        .Replace("{ligue}", string.Join(" / ", Config.Ligues))
        .Replace("{langage}", Config.Langage)
        .Replace("{fichier}", Config.Bot.Fichier);
}
