namespace CodinGame.Core.ResourceGuard;

/// <summary>
/// Comptabilité des tokens et des dollars consommés.
/// Le comptage ne dépend d'aucun agent : un agent ne peut ni s'auto-déclarer économe,
/// ni dépasser son plafond.
///
/// Intercepte les appels LLM, écrit les coûts dans l'ExperimentStore,
/// publie les métriques OTLP, et applique les plafonds.
///
/// Voir .Agent/DETERMINISTIC_LAYER.md §2.H et .Agent/PARAMETERS.md §5-§6.
/// </summary>
public sealed class BudgetGuard
{
    private readonly decimal _monthlyBudgetUsd;
    private readonly double _cycleCapFraction;
    private readonly double _alertThreshold;
    private decimal _totalSpentUsd;
    private readonly object _lock = new();

    /// <param name="monthlyBudgetUsd">Budget mensuel total (ex: 250 USD). Voir PARAMETERS.md §6.</param>
    /// <param name="cycleCapFraction">Fraction du budget max par cycle (ex: 0.01 = 1%).</param>
    /// <param name="alertThreshold">Seuil d'alerte (ex: 0.80 = 80% du budget consommé).</param>
    public BudgetGuard(
        decimal monthlyBudgetUsd = 250m,
        double cycleCapFraction = 0.01,
        double alertThreshold = 0.80)
    {
        _monthlyBudgetUsd = monthlyBudgetUsd;
        _cycleCapFraction = cycleCapFraction;
        _alertThreshold = alertThreshold;
    }

    /// <summary>Budget consommé ce mois-ci.</summary>
    public decimal TotalSpentUsd
    {
        get { lock (_lock) { return _totalSpentUsd; } }
    }

    /// <summary>Fraction du budget mensuel consommée (0..1).</summary>
    public double BudgetFraction
    {
        get { lock (_lock) { return (double)(_totalSpentUsd / _monthlyBudgetUsd); } }
    }

    /// <summary>Plafond de coût pour un cycle individuel.</summary>
    public decimal CycleCapUsd => _monthlyBudgetUsd * (decimal)_cycleCapFraction;

    /// <summary>Vérifie si le budget mensuel est épuisé.</summary>
    public bool IsBudgetExhausted
    {
        get { lock (_lock) { return _totalSpentUsd >= _monthlyBudgetUsd; } }
    }

    /// <summary>Vérifie si le seuil d'alerte est atteint.</summary>
    public bool IsAlertThresholdReached
    {
        get { lock (_lock) { return (double)(_totalSpentUsd / _monthlyBudgetUsd) >= _alertThreshold; } }
    }

    /// <summary>
    /// Enregistre un coût. Retourne false si le plafond de cycle ou le budget mensuel est dépassé.
    /// </summary>
    /// <param name="costUsd">Coût de l'appel en USD.</param>
    /// <param name="cycleSpentUsd">Coût déjà dépensé dans le cycle courant.</param>
    public bool RecordCost(decimal costUsd, decimal cycleSpentUsd)
    {
        lock (_lock)
        {
            if (_totalSpentUsd + costUsd > _monthlyBudgetUsd)
                return false;

            if (cycleSpentUsd + costUsd > CycleCapUsd)
                return false;

            _totalSpentUsd += costUsd;
            return true;
        }
    }

    /// <summary>Réinitialisation mensuelle du compteur.</summary>
    public void ResetMonthly()
    {
        lock (_lock) { _totalSpentUsd = 0; }
    }
}
