namespace CodinGame.Core.ResourceGuard;

/// <summary>
/// Gestionnaire de rétention et de quotas disque.
/// Évite l'incident historique de saturation disque (voir STRATEGY_ANALYSIS.md §3 incident 1).
///
/// Règles de rétention : définies en PARAMETERS.md §7. Le composant les applique, il ne les décide pas.
///
/// Voir .Agent/DETERMINISTIC_LAYER.md §2.D.
/// </summary>
public sealed class DiskQuotaManager
{
    private readonly int _maxNotableReplays;
    private readonly long _maxLogSizeBytes;

    /// <param name="maxNotableReplays">Nombre max de replays de défaites notables conservés par pipeline (ex: 5).</param>
    /// <param name="maxLogSizeMb">Taille max de logs texte par pipeline en Mo (ex: 50).</param>
    public DiskQuotaManager(int maxNotableReplays = 5, int maxLogSizeMb = 50)
    {
        _maxNotableReplays = maxNotableReplays;
        _maxLogSizeBytes = maxLogSizeMb * 1024L * 1024L;
    }

    /// <summary>
    /// Purge les replays de matchs ordinaires (non notables) après évaluation.
    /// Conserve uniquement les N dernières défaites notables pour le Researcher.
    /// </summary>
    public Task PurgeReplaysAsync(string pipelinePath, CancellationToken ct = default)
    {
        string replaysDir = Path.Combine(pipelinePath, "replays");
        if (!Directory.Exists(replaysDir))
            return Task.CompletedTask;

        // TODO Phase 2 : implémenter la purge réelle
        //   - Supprimer tous les replays ordinaires
        //   - Garder les _maxNotableReplays défaites les plus récentes
        //   - Référencer les replays conservés dans l'ExperimentStore

        return Task.CompletedTask;
    }

    /// <summary>
    /// Rotation des logs texte : tronque les fichiers dépassant le plafond.
    /// </summary>
    public Task RotateLogsAsync(string pipelinePath, CancellationToken ct = default)
    {
        string logsDir = Path.Combine(pipelinePath, "logs");
        if (!Directory.Exists(logsDir))
            return Task.CompletedTask;

        // TODO Phase 2 : implémenter la rotation réelle
        //   - Pour chaque fichier de log, si taille > _maxLogSizeBytes, tronquer

        return Task.CompletedTask;
    }
}
