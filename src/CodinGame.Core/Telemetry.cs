using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CodinGame.Core;

/// <summary>
/// Traces et métriques de la ferme, affichées par le dashboard Aspire.
/// Les noms commencent par « CodinGame. » pour être captés par les ServiceDefaults.
/// </summary>
public static class Telemetry
{
    public const string Name = "CodinGame.Farm";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> LocalGames =
        Meter.CreateCounter<long>("farm.parties.locales", description: "Parties jouées dans le simulateur");

    public static readonly Counter<long> ApiGames =
        Meter.CreateCounter<long>("farm.parties.api", description: "Parties de test jouées sur CodinGame");

    public static readonly Counter<long> Trials =
        Meter.CreateCounter<long>("farm.essais", description: "Essais terminés, par verdict");

    public static readonly Counter<long> AgentTokens =
        Meter.CreateCounter<long>("farm.agent.tokens", description: "Tokens consommés par les agents, par rôle");

    public static readonly Histogram<double> AgentSeconds =
        Meter.CreateHistogram<double>("farm.agent.duree", unit: "s", description: "Durée d'un appel d'agent");

    private static long _apiTestGamesLeft = -1;
    private static long _championWins;
    private static long _championGames;
    private static long _trialNumber;

    static Telemetry()
    {
        Meter.CreateObservableGauge("farm.api.parties_restantes",
            () => Interlocked.Read(ref _apiTestGamesLeft),
            description: "Parties de test CodinGame restantes sur la fenêtre la plus contraignante");
        Meter.CreateObservableGauge("farm.champion.victoires_contre_boss",
            () => Interlocked.Read(ref _championGames) == 0
                ? 0.0
                : (double)Interlocked.Read(ref _championWins) / Interlocked.Read(ref _championGames),
            description: "Taux de victoire du champion contre le boss, sur CodinGame");
        Meter.CreateObservableGauge("farm.essai.numero",
            () => Interlocked.Read(ref _trialNumber),
            description: "Numéro du dernier essai lancé");
    }

    public static void SetApiTestGamesLeft(long value) => Interlocked.Exchange(ref _apiTestGamesLeft, value);

    public static void SetChampionScore(long wins, long games)
    {
        Interlocked.Exchange(ref _championWins, wins);
        Interlocked.Exchange(ref _championGames, games);
    }

    public static void SetTrialNumber(long value) => Interlocked.Exchange(ref _trialNumber, value);
}
