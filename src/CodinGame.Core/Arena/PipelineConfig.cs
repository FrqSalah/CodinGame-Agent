namespace CodinGame.Core.Arena;

/// <summary>
/// Configuration des timeouts pour un jeu donné.
/// Les valeurs sont lues depuis la configuration du pipeline — jamais codées en dur.
/// Voir .Agent/PARAMETERS.md §2.
/// </summary>
public sealed class GameTimeoutConfig
{
    /// <summary>Temps alloué au premier tour (initialisation), en millisecondes.</summary>
    public required int FirstTurnTimeoutMs { get; init; }

    /// <summary>Temps alloué à chaque tour suivant, en millisecondes.</summary>
    public required int TurnTimeoutMs { get; init; }
}

/// <summary>
/// Configuration d'un pipeline de jeu.
/// </summary>
public sealed class PipelineConfig
{
    public required string GameName { get; init; }
    public required string PipelineId { get; init; }
    public required GameTimeoutConfig Timeouts { get; init; }

    /// <summary>Chemin vers l'exécutable du referee.</summary>
    public required string RefereePath { get; init; }

    /// <summary>Chemin vers le dépôt Git local de ce pipeline.</summary>
    public required string RepositoryPath { get; init; }
}
