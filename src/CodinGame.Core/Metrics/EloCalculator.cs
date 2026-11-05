namespace CodinGame.Core.Metrics;

/// <summary>
/// Verdict produit par le test SPRT.
/// Voir .Agent/PARAMETERS.md §1.
/// </summary>
public enum SprtVerdict
{
    /// <summary>H0 acceptée : aucun gain significatif détecté.</summary>
    AcceptH0,

    /// <summary>H1 acceptée : gain >= borne H1 établi avec confiance.</summary>
    AcceptH1,

    /// <summary>Plafond de matchs atteint sans verdict. Correspond à TUNE_PARAMS.</summary>
    Inconclusive
}

/// <summary>
/// Résultat complet d'une évaluation Elo + SPRT.
/// </summary>
public sealed record EloEvaluationResult(
    double AnchorElo,
    double TrainingElo,
    double WinRate,
    int MatchesPlayed,
    SprtVerdict Verdict,
    int Timeouts,
    int Crashes
);

/// <summary>
/// Calcul Elo et test SPRT.
///
/// Deux mesures distinctes, à ne jamais confondre :
///   - Elo d'ancrage : contre le pool gelé, seule mesure comparable entre cycles.
///   - Elo d'entraînement : contre le pool évolutif, non comparable entre cycles.
///
/// Voir .Agent/DETERMINISTIC_LAYER.md §2.C et .Agent/PARAMETERS.md §1.
///
/// Décision ouverte : mise à jour itérative (facteur K) vs Bradley-Terry (maximum de
/// vraisemblance). Voir DETERMINISTIC_LAYER.md §2.C.
/// </summary>
public sealed class EloCalculator
{
    private readonly double _h0Elo;
    private readonly double _h1Elo;
    private readonly double _alpha;
    private readonly double _beta;
    private readonly int _minMatches;
    private readonly int _maxMatches;

    /// <param name="h0Elo">Borne H0 du SPRT (ex: 0 = aucun gain). Voir PARAMETERS.md §1.</param>
    /// <param name="h1Elo">Borne H1 du SPRT (ex: 25 = seuil d'effet minimal).</param>
    /// <param name="alpha">Risque de faux positif (ex: 0.05).</param>
    /// <param name="beta">Risque de faux négatif (ex: 0.05).</param>
    /// <param name="minMatches">Plancher avant toute décision (ex: 100).</param>
    /// <param name="maxMatches">Plafond de coût (ex: 2000).</param>
    public EloCalculator(
        double h0Elo = 0,
        double h1Elo = 25,
        double alpha = 0.05,
        double beta = 0.05,
        int minMatches = 100,
        int maxMatches = 2000)
    {
        _h0Elo = h0Elo;
        _h1Elo = h1Elo;
        _alpha = alpha;
        _beta = beta;
        _minMatches = minMatches;
        _maxMatches = maxMatches;
    }

    /// <summary>
    /// Espérance de score selon la formule Elo standard.
    ///   E_A = 1 / (1 + 10^((R_B - R_A) / 400))
    /// </summary>
    public static double ExpectedScore(double ratingA, double ratingB)
    {
        return 1.0 / (1.0 + Math.Pow(10.0, (ratingB - ratingA) / 400.0));
    }

    /// <summary>
    /// Convertit un winrate (0..1) en différence Elo.
    /// </summary>
    public static double WinRateToEloDiff(double winRate)
    {
        if (winRate <= 0 || winRate >= 1)
            throw new ArgumentOutOfRangeException(nameof(winRate), "Must be in (0, 1)");
        return -400.0 * Math.Log10(1.0 / winRate - 1.0);
    }

    /// <summary>
    /// Convertit une différence Elo en winrate attendu.
    /// </summary>
    public static double EloDiffToWinRate(double eloDiff)
    {
        return 1.0 / (1.0 + Math.Pow(10.0, -eloDiff / 400.0));
    }

    /// <summary>
    /// Évalue le verdict SPRT à partir d'un compteur de victoires/défaites/nuls.
    /// </summary>
    /// <param name="wins">Nombre de victoires du bot testé.</param>
    /// <param name="losses">Nombre de défaites.</param>
    /// <param name="draws">Nombre de nuls.</param>
    public SprtVerdict EvaluateSprt(int wins, int losses, int draws)
    {
        int total = wins + losses + draws;

        if (total < _minMatches)
            return SprtVerdict.Inconclusive;

        if (total >= _maxMatches)
            return SprtVerdict.Inconclusive;

        double score = (wins + 0.5 * draws) / total;

        // Log-likelihood ratio sous H0 et H1
        double p0 = EloDiffToWinRate(_h0Elo);
        double p1 = EloDiffToWinRate(_h1Elo);

        double llr = LogLikelihoodRatio(score, total, p0, p1);

        double lowerBound = Math.Log(_beta / (1.0 - _alpha));
        double upperBound = Math.Log((1.0 - _beta) / _alpha);

        if (llr >= upperBound)
            return SprtVerdict.AcceptH1;
        if (llr <= lowerBound)
            return SprtVerdict.AcceptH0;

        return SprtVerdict.Inconclusive;
    }

    private static double LogLikelihoodRatio(double observedScore, int n, double p0, double p1)
    {
        // Approximation via distribution normale du score moyen
        if (observedScore <= 0 || observedScore >= 1)
            observedScore = Math.Clamp(observedScore, 0.001, 0.999);

        double ll0 = n * (observedScore * Math.Log(p0) + (1.0 - observedScore) * Math.Log(1.0 - p0));
        double ll1 = n * (observedScore * Math.Log(p1) + (1.0 - observedScore) * Math.Log(1.0 - p1));

        return ll1 - ll0;
    }
}
