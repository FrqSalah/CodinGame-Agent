namespace CodinGame.Core.Arena;

/// <summary>
/// Ordonnanceur global des créneaux CPU pour l'arène.
/// C'est le composant qui rend les mesures valides — le plus critique de la couche déterministe.
///
/// Voir .Agent/DETERMINISTIC_LAYER.md §2.F et .Agent/PARAMETERS.md §3.
///
/// L'incident 2 (STRATEGY_ANALYSIS.md) a montré qu'un tournoi non isolé ne produit pas
/// des résultats dégradés mais des résultats FAUX. Sans ordonnanceur global, 40 pipelines
/// sur 16 cœurs reproduisent l'incident à l'identique.
/// </summary>
public sealed class ArenaScheduler : IDisposable
{
    private readonly SemaphoreSlim _slotSemaphore;
    private readonly int _coresPerSlot;
    private readonly int[] _arenaCores;
    private readonly Queue<int[]> _availableSlots = new();
    private readonly object _lock = new();

    /// <param name="arenaCores">Indices des cœurs CPU dédiés à l'arène (ex: 2..15).</param>
    /// <param name="coresPerSlot">Nombre de cœurs par créneau de match (ex: 2).</param>
    /// <param name="maxConcurrentTournaments">Plafond de tournois simultanés (ex: 4).</param>
    public ArenaScheduler(int[] arenaCores, int coresPerSlot, int maxConcurrentTournaments)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(coresPerSlot, 0);

        _arenaCores = arenaCores;
        _coresPerSlot = coresPerSlot;

        // Constituer les créneaux à partir des cœurs disponibles
        int totalSlots = arenaCores.Length / coresPerSlot;
        for (int i = 0; i < totalSlots; i++)
        {
            var slot = new int[coresPerSlot];
            Array.Copy(arenaCores, i * coresPerSlot, slot, 0, coresPerSlot);
            _availableSlots.Enqueue(slot);
        }

        // Le sémaphore borne au minimum des créneaux physiques et du plafond logique
        int effectiveSlots = Math.Min(totalSlots, maxConcurrentTournaments);
        _slotSemaphore = new SemaphoreSlim(effectiveSlots, effectiveSlots);
    }

    /// <summary>
    /// Demande un créneau CPU. Attend si aucun n'est disponible — jamais d'exécution en mode dégradé.
    /// </summary>
    public async Task<ArenaSlot> AcquireSlotAsync(string pipelineId, CancellationToken ct = default)
    {
        await _slotSemaphore.WaitAsync(ct);

        int[] cores;
        lock (_lock)
        {
            cores = _availableSlots.Dequeue();
        }

        return new ArenaSlot(cores, pipelineId, this);
    }

    internal void ReleaseSlot(int[] cores)
    {
        lock (_lock)
        {
            _availableSlots.Enqueue(cores);
        }
        _slotSemaphore.Release();
    }

    public void Dispose() => _slotSemaphore.Dispose();
}

/// <summary>
/// Créneau CPU attribué par l'ArenaScheduler. Dispose libère le créneau.
/// </summary>
public sealed class ArenaSlot : IDisposable
{
    private readonly ArenaScheduler _scheduler;
    private bool _disposed;

    public int[] Cores { get; }
    public string PipelineId { get; }

    internal ArenaSlot(int[] cores, string pipelineId, ArenaScheduler scheduler)
    {
        Cores = cores;
        PipelineId = pipelineId;
        _scheduler = scheduler;
    }

    /// <summary>Masque d'affinité processeur à appliquer via Process.ProcessorAffinity.</summary>
    public nint AffinityMask
    {
        get
        {
            nint mask = 0;
            foreach (int core in Cores)
                mask |= (nint)(1L << core);
            return mask;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scheduler.ReleaseSlot(Cores);
    }
}
