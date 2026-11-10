namespace CodinGame.Core.Judging;

public enum SprtDecision
{
    Continue,

    /// <summary>Le candidat est meilleur (hypothèse H1 acceptée).</summary>
    Better,

    /// <summary>Le candidat n'est pas meilleur (hypothèse H0 acceptée).</summary>
    NotBetter,
}

/// <summary>
/// Test séquentiel de Wald sur les victoires et défaites du candidat contre le champion
/// (méthode standard des moteurs d'échecs). H0 : écart ≤ <paramref name="elo0"/> ;
/// H1 : écart ≥ <paramref name="elo1"/>. Les égalités ne comptent pas.
/// </summary>
public sealed class Sprt(double elo0, double elo1, double alpha, double beta)
{
    private readonly double _winTerm = Math.Log(Expected(elo1) / Expected(elo0));
    private readonly double _lossTerm = Math.Log((1 - Expected(elo1)) / (1 - Expected(elo0)));

    public double LowerBound { get; } = Math.Log(beta / (1 - alpha));
    public double UpperBound { get; } = Math.Log((1 - beta) / alpha);

    /// <summary>Rapport de vraisemblance logarithmique après ces résultats.</summary>
    public double Llr(int wins, int losses) => (wins * _winTerm) + (losses * _lossTerm);

    public SprtDecision Decide(int wins, int losses)
    {
        var llr = Llr(wins, losses);
        return llr >= UpperBound ? SprtDecision.Better
             : llr <= LowerBound ? SprtDecision.NotBetter
             : SprtDecision.Continue;
    }

    /// <summary>Probabilité de victoire attendue pour un écart Elo donné.</summary>
    public static double Expected(double elo) => 1.0 / (1.0 + Math.Pow(10, -elo / 400.0));
}
