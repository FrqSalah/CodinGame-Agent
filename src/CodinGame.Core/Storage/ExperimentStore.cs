namespace CodinGame.Core.Storage;

/// <summary>
/// Source de vérité unique de toutes les données chiffrées du système.
/// En cas de divergence avec un message de commit, un nom de tag ou un log, 
/// c'est matches.db qui a raison.
///
/// Base : SQLite léger, un fichier matches.db par pipeline.
///
/// Voir .Agent/DETERMINISTIC_LAYER.md §2.E.
/// </summary>
public sealed class ExperimentStore : IDisposable
{
    private readonly string _dbPath;

    // TODO Phase 2 : remplacer par une vraie connexion SQLite (Microsoft.Data.Sqlite)

    public ExperimentStore(string pipelinePath)
    {
        _dbPath = Path.Combine(pipelinePath, "matches.db");
    }

    /// <summary>
    /// Initialise le schéma de la base de données si nécessaire.
    /// Tables : Experiments, MatchResults, Metrics, Decisions, Costs, PoolSnapshots.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // TODO Phase 2 : créer les tables SQLite
        // Identifiant partagé : experimentId (= nom de branche et de tag Git)
        //
        // Tables prévues :
        //   Experiments : experimentId, branche, commit, hash du code, seeds, horodatage
        //   MatchResults : paires de matchs, issues, timeouts, crashs, temps par tour
        //   Metrics : Elo d'ancrage, Elo d'entraînement, winrate, verdict SPRT
        //   Decisions : verdict du Reviewer, motif, agent émetteur
        //   Costs : tokens et dollars consommés par cycle et par agent
        //   PoolSnapshots : composition du pool d'ancrage et d'entraînement à chaque cycle

        return Task.CompletedTask;
    }

    /// <summary>
    /// Enregistre le début d'une nouvelle expérience.
    /// </summary>
    public Task RecordExperimentStartAsync(
        string experimentId, string commitHash, long[] seeds, CancellationToken ct = default)
    {
        // TODO Phase 2
        return Task.CompletedTask;
    }

    /// <summary>
    /// Enregistre le verdict final d'une expérience.
    /// </summary>
    public Task RecordDecisionAsync(
        string experimentId, string verdict, string reason, string agentName,
        CancellationToken ct = default)
    {
        // TODO Phase 2
        return Task.CompletedTask;
    }

    /// <summary>
    /// Enregistre les coûts de tokens d'un cycle.
    /// </summary>
    public Task RecordCostAsync(
        string experimentId, string agentName, int tokensUsed, decimal costUsd,
        CancellationToken ct = default)
    {
        // TODO Phase 2
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // TODO Phase 2 : fermer la connexion SQLite
    }
}
