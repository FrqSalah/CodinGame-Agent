namespace CodinGame.Core.Arena;

/// <summary>
/// Propriétaire unique des pools d'adversaires. Aucun agent ne modifie un pool
/// directement ; les agents émettent une demande, ce composant applique la politique.
///
/// Voir .Agent/DETERMINISTIC_LAYER.md §2.G.
/// </summary>
public sealed class OpponentPoolManager
{
    private readonly List<string> _anchorPool = [];
    private readonly List<string> _trainingPool = [];
    private readonly object _lock = new();
    private bool _anchorFrozen;

    /// <summary>Pool d'ancrage gelé — ne change jamais après initialisation.</summary>
    public IReadOnlyList<string> AnchorPool
    {
        get { lock (_lock) { return _anchorPool.ToList().AsReadOnly(); } }
    }

    /// <summary>Pool d'entraînement évolutif.</summary>
    public IReadOnlyList<string> TrainingPool
    {
        get { lock (_lock) { return _trainingPool.ToList().AsReadOnly(); } }
    }

    /// <summary>
    /// Constitue le pool d'ancrage initial et le gèle définitivement.
    /// Appelé une fois à la création du pipeline par l'Admin.
    /// </summary>
    public void InitializeAnchorPool(IEnumerable<string> botPaths)
    {
        lock (_lock)
        {
            if (_anchorFrozen)
                throw new InvalidOperationException(
                    "Le pool d'ancrage est gelé. Le modifier invaliderait tout l'historique Elo.");

            _anchorPool.Clear();
            _anchorPool.AddRange(botPaths);
            _anchorFrozen = true;

            // Le pool d'entraînement démarre avec les mêmes adversaires
            _trainingPool.Clear();
            _trainingPool.AddRange(botPaths);
        }
    }

    /// <summary>
    /// Enrichit le pool d'entraînement avec un nouveau checkpoint.
    /// Appelé sur demande du Reviewer (après PROMOTE) ou du Researcher.
    /// </summary>
    /// <param name="botPath">Chemin vers le bot à ajouter.</param>
    /// <param name="reason">Motif journalisé dans l'ExperimentStore.</param>
    /// <param name="maxPoolSize">Taille cible du pool ; les checkpoints les plus anciens sont retirés.</param>
    public void EnrichTrainingPool(string botPath, string reason, int maxPoolSize = 10)
    {
        lock (_lock)
        {
            _trainingPool.Add(botPath);

            // Garder une diversité de checkpoints (anciens + récents)
            while (_trainingPool.Count > maxPoolSize)
            {
                // Retirer le plus ancien (après les adversaires d'ancrage)
                int anchorCount = _anchorPool.Count;
                if (_trainingPool.Count > anchorCount)
                {
                    _trainingPool.RemoveAt(anchorCount);
                }
                else
                {
                    break;
                }
            }
        }
    }
}
