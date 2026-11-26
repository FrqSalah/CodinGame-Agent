using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodinGame.Core.Config;

/// <param name="Modele">Le modèle le plus fort, utilisé tant qu'il répond.</param>
/// <param name="Secours">Les modèles suivants, du plus fort au moins fort, pris quand les précédents sont à leur limite.</param>
public sealed record AgentRole(string Outil, string Modele, int MinutesMax, IReadOnlyList<string> Secours)
{
    /// <summary>La chaîne complète, du plus fort au moins fort.</summary>
    public IReadOnlyList<string> Chain() => [Modele, .. Secours.Where(s => s != Modele)];
}

/// <param name="BlocageMinutes">Durée pendant laquelle un modèle à sa limite est écarté avant d'être réessayé.</param>
public sealed record AgentRoles(AgentRole Penseur, AgentRole Codeur, AgentRole RegardNeuf, int BlocageMinutes);

public sealed record BotSettings(string Fichier, string Compilation);

/// <param name="PartiesProlongation">Second round: a duel that reaches <paramref name="PartiesMax"/> games without a verdict goes on up to this many games...</param>
/// <param name="LlrProlongation">... when the second round's test already leans that way (its log-likelihood ratio at least this)...</param>
/// <param name="Elo1Prolongation">... judged with this lower bar: small gains need more games to be proven.</param>
public sealed record ArenaSettings(
    string Arbitre,
    int Parallele,
    int PremierTourMs,
    int TourMs,
    double ToleranceTemps,
    int PartiesMin,
    int PartiesMax,
    double Elo0,
    double Elo1,
    double Alpha,
    double Beta,
    int PartiesProlongation,
    double LlrProlongation,
    double Elo1Prolongation)
{
    /// <summary>The time limits of a local game's turns, tolerance included.</summary>
    public Arena.TurnLimits Limits() =>
        new(TimeSpan.FromMilliseconds(PremierTourMs * ToleranceTemps), TimeSpan.FromMilliseconds(TourMs * ToleranceTemps));
}

/// <param name="PartiesConfirmation">Parties de confirmation par adversaire, pour le candidat comme pour le champion.</param>
/// <param name="Adversaire">Adversaire de confirmation quand <paramref name="ConfirmationJoueurs"/> vaut 0.</param>
/// <param name="ConfirmationJoueurs">Nombre de vrais joueurs, classés au-dessus de nous, contre qui confirmer (0 : <paramref name="Adversaire"/> seul).</param>
/// <param name="ConfirmationRangsAuDessus">Ces joueurs sont pris au plus à ce nombre de rangs au-dessus du nôtre.</param>
/// <param name="RetourSiBaisse">
/// Rollback: once a champion's submission is fully ranked, if its score is below the best recent
/// champion's (<paramref name="RetourJours"/>) by more than this many points, that champion is
/// restored and resubmitted. 0 disables it.
/// </param>
/// <param name="ConfirmationAlpha">
/// A candidate is rejected by the real players only when it does clearly worse than the champion:
/// a candidate as good would lose that many more of the games they split at most this often (sign test).
/// </param>
/// <param name="RetourJours">
/// The rollback compares with the best of the previous champion and of those promoted in this many
/// days before it (0: the previous champion only). Older scores are left out: the leaderboard drifts.
/// </param>
public sealed record ApiSettings(
    int PartiesConfirmation, string Adversaire, int ConfirmationJoueurs, int ConfirmationRangsAuDessus,
    bool SoumettreChampions, int ReleveClassementMinutes, double RetourSiBaisse, double ConfirmationAlpha,
    int RetourJours)
{
    /// <summary>Parties de test que coûte la confirmation d'un candidat, champion compris.</summary>
    public int ConfirmationCost => 2 * PartiesConfirmation * Math.Max(1, ConfirmationJoueurs);
}

public sealed record LoopSettings(
    bool Actif,
    int EssaisParJour,
    int RejetsAvantRegardNeuf,
    int CorrectionsCompilationMax,
    int RelancesReponseVide,
    int PauseMinutesSiEchecAgent,
    int PauseMinutesSiInactif,
    int MotsNoteMax);

public sealed record ContextSettings(int EssaisResumes, int IdeesRejetees);

/// <summary>
/// Coder's lab: after each version, quick games against the champion. A fault or a win rate
/// below <paramref name="SeuilRevision"/> sends the result back to the coder, up to
/// <paramref name="RevisionsMax"/> times, before the judge. <paramref name="Parties"/> = 0 disables it.
/// </summary>
public sealed record LabSettings(int Parties, int RevisionsMax, double SeuilRevision);

/// <summary>
/// Loss analysis (2026-11-19). <paramref name="SeuilChoc"/>: distance between a pod's next state
/// and its no-collision prediction above which it collided. A lost game is "blocking" when our
/// runner was hit at least <paramref name="EcartChocs"/> times more than theirs, "speed" when it
/// took at least <paramref name="EcartCadence"/> more turns per checkpoint on collision-free
/// stretches, "finish" when lost by at most <paramref name="ArriveeSerree"/> checkpoints.
/// <paramref name="PartiesClasseesParReleve"/>: ranked games read at each ranking (0: none).
/// </summary>
public sealed record AnalysisSettings(double SeuilChoc, int EcartChocs, double EcartCadence, int ArriveeSerree, int PartiesClasseesParReleve);

/// <summary>
/// Local bench: candidate and champion play the same races against these opponents and are compared
/// race by race. <paramref name="Adversaires"/>: sources of bots published by other players, built with
/// <paramref name="Compilation"/>. <paramref name="AnciensChampions"/>: the former champions with the
/// best real scores join them, taken from the journal (0: none). <paramref name="Alpha"/>: a candidate
/// is rejected only when it does clearly worse than the champion (sign test). No opponent: no bench.
/// </summary>
public sealed record BenchSettings(IReadOnlyList<string> Adversaires, int PartiesParAdversaire, string Compilation, int AnciensChampions, double Alpha);

/// <summary>
/// Réglages d'une pipeline (<c>pipeline.json</c>). Relus au début de chaque essai :
/// modifier le fichier suffit, sans recompiler ni redémarrer.
/// </summary>
public sealed record PipelineConfig(
    string Jeu,
    string Langage,
    IReadOnlyList<string> Ligues,
    BotSettings Bot,
    AgentRoles Agents,
    ArenaSettings Arene,
    ApiSettings Api,
    LoopSettings Boucle,
    ContextSettings Contexte,
    BenchSettings Banc,
    LabSettings Laboratoire,
    AnalysisSettings Analyse)
{
    /// <summary>Outils d'IA pris en charge.</summary>
    public static readonly string[] SupportedTools = ["agy"];

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    /// <summary>Règles que le format JSON ne peut pas exprimer. Chaque erreur nomme son champ.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Check(bool ok, string field, string message)
        {
            if (!ok)
            {
                errors.Add($"{field} : {message}");
            }
        }

        Check(!string.IsNullOrWhiteSpace(Jeu), "jeu", "obligatoire");
        Check(!string.IsNullOrWhiteSpace(Langage), "langage", "obligatoire");
        Check(Ligues is { Count: > 0 }, "ligues", "au moins une ligue couverte par le moteur");
        Check(!string.IsNullOrWhiteSpace(Bot.Fichier), "bot.fichier", "obligatoire");
        Check(Bot.Compilation.Contains("{source}") && Bot.Compilation.Contains("{sortie}"),
            "bot.compilation", "doit contenir {source} et {sortie}");

        foreach (var (name, role) in new[] { ("penseur", Agents.Penseur), ("codeur", Agents.Codeur), ("regard_neuf", Agents.RegardNeuf) })
        {
            Check(SupportedTools.Contains(role.Outil), $"agents.{name}.outil", $"« {role.Outil} » inconnu ; valeurs possibles : {string.Join(", ", SupportedTools)}");
            Check(!string.IsNullOrWhiteSpace(role.Modele), $"agents.{name}.modele", "obligatoire");
            Check(role.MinutesMax is >= 1 and <= 120, $"agents.{name}.minutes_max", "entre 1 et 120");
            Check(role.Secours.All(s => !string.IsNullOrWhiteSpace(s)), $"agents.{name}.secours", "noms de modèles non vides");
        }

        Check(!string.IsNullOrWhiteSpace(Arene.Arbitre), "arene.arbitre", "commande du moteur obligatoire");
        Check(Agents.BlocageMinutes >= 1, "agents.blocage_minutes", "au moins 1");
        Check(Arene.Parallele is >= 1 and <= 64, "arene.parallele", "entre 1 et 64");
        Check(Arene.PremierTourMs > 0 && Arene.TourMs > 0, "arene.premier_tour_ms / tour_ms", "doivent être positifs");
        Check(Arene.ToleranceTemps >= 1, "arene.tolerance_temps", "au moins 1");
        Check(Arene.PartiesMin >= 2 && Arene.PartiesMax >= Arene.PartiesMin, "arene.parties_min / parties_max", "il faut 2 ≤ min ≤ max");
        Check(Arene.Elo1 > Arene.Elo0, "arene.elo1", "doit dépasser arene.elo0");
        Check(Arene.PartiesProlongation >= Arene.PartiesMax, "arene.parties_prolongation", "au moins arene.parties_max (égal = jamais de prolongation)");
        Check(Arene.LlrProlongation >= 0, "arene.llr_prolongation", "positif ou nul");
        Check(Arene.Elo1Prolongation > Arene.Elo0 && Arene.Elo1Prolongation <= Arene.Elo1, "arene.elo1_prolongation", "entre arene.elo0 (exclu) et arene.elo1");
        Check(Arene.Alpha is > 0 and < 0.5 && Arene.Beta is > 0 and < 0.5, "arene.alpha / beta", "entre 0 et 0,5");
        Check(Api.PartiesConfirmation >= 0, "api.parties_confirmation", "positif ou nul");
        Check(!string.IsNullOrWhiteSpace(Api.Adversaire), "api.adversaire", "obligatoire (« boss » ou un agent_id)");
        Check(Api.ConfirmationJoueurs >= 0, "api.confirmation_joueurs", "positif ou nul");
        Check(Api.RetourSiBaisse >= 0, "api.retour_si_baisse", "positif ou nul (0 : jamais de retour)");
        Check(Api.ConfirmationAlpha is > 0 and < 1, "api.confirmation_alpha", "entre 0 et 1 exclus");
        Check(Api.RetourJours >= 0, "api.retour_jours", "positif ou nul (0 : le champion précédent seul)");
        Check(Laboratoire.Parties >= 0, "laboratoire.parties", "positif ou nul (0 : pas de laboratoire)");
        Check(Laboratoire.RevisionsMax >= 0, "laboratoire.revisions_max", "positif ou nul");
        Check(Laboratoire.SeuilRevision is >= 0 and <= 1, "laboratoire.seuil_revision", "entre 0 et 1");
        Check(Analyse.SeuilChoc > 0, "analyse.seuil_choc", "positif");
        Check(Analyse.EcartChocs >= 1, "analyse.ecart_chocs", "au moins 1");
        Check(Analyse.EcartCadence > 0, "analyse.ecart_cadence", "positif");
        Check(Analyse.ArriveeSerree >= 0, "analyse.arrivee_serree", "positif ou nul");
        Check(Analyse.PartiesClasseesParReleve >= 0, "analyse.parties_classees_par_releve", "positif ou nul (0 : aucune)");
        Check(Api.ConfirmationJoueurs == 0 || Api.ConfirmationRangsAuDessus >= Api.ConfirmationJoueurs,
            "api.confirmation_rangs_au_dessus", "au moins api.confirmation_joueurs");
        Check(Boucle.EssaisParJour >= 0, "boucle.essais_par_jour", "positif ou nul");
        Check(Boucle.RejetsAvantRegardNeuf >= 1, "boucle.rejets_avant_regard_neuf", "au moins 1");
        Check(Boucle.CorrectionsCompilationMax >= 0, "boucle.corrections_compilation_max", "positif ou nul");
        Check(Boucle.RelancesReponseVide is >= 0 and <= 5, "boucle.relances_reponse_vide", "entre 0 et 5");
        Check(Boucle.MotsNoteMax >= 50, "boucle.mots_note_max", "au moins 50");
        Check(Boucle.PauseMinutesSiEchecAgent >= 1 && Boucle.PauseMinutesSiInactif >= 1, "boucle.pause_minutes_*", "au moins 1");
        Check(Contexte.EssaisResumes >= 1, "contexte.essais_resumes", "au moins 1");
        Check(Contexte.IdeesRejetees >= 1, "contexte.idees_rejetees", "au moins 1");
        Check(Banc.Compilation.Contains("{source}") && Banc.Compilation.Contains("{sortie}"), "banc.compilation", "doit contenir {source} et {sortie}");
        Check(Banc.PartiesParAdversaire >= 2 && Banc.PartiesParAdversaire % 2 == 0, "banc.parties_par_adversaire", "pair, au moins 2 (chaque circuit est joué des deux places)");
        Check(Banc.AnciensChampions >= 0, "banc.anciens_champions", "positif ou nul (0 : aucun ancien champion)");
        Check(Banc.Alpha is > 0 and < 1, "banc.alpha", "entre 0 et 1 exclus");
        return errors;
    }
}

public sealed class ConfigException(string message) : Exception(message);

/// <summary>
/// Charge la configuration : celle livrée avec l'application (graine), recouverte par celle
/// du dossier de données, que l'utilisateur modifie. À la première lecture, la graine est
/// copiée dans le dossier de données ; ensuite, seules les clés nouvelles y sont ajoutées.
/// </summary>
/// <summary>
/// The live configuration is the shipped one (the seed) overlaid by the user's values. A value never
/// changed by hand follows the seed: the seed last applied is kept next to the live file
/// (<see cref="DefaultsPath"/>), and a live value still equal to it takes the new default.
/// </summary>
public static class ConfigLoader
{
    /// <summary>The seed that the live file <paramref name="dataPath"/> was last brought up to.</summary>
    public static string DefaultsPath(string dataPath) =>
        Path.Combine(Path.GetDirectoryName(dataPath)!, Path.GetFileNameWithoutExtension(dataPath) + ".defauts.json");

    public static PipelineConfig Load(string seedPath, string dataPath, List<string>? notes = null)
    {
        var seed = ParseObject(seedPath);
        var defaultsPath = DefaultsPath(dataPath);
        if (!File.Exists(dataPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
            File.WriteAllText(dataPath, seed.ToJsonString(PipelineConfig.Json));
            notes?.Add($"configuration créée : {dataPath}");
        }

        // Without the seed last applied (first start), every live value counts as set by hand.
        var user = ParseObject(dataPath);
        var previous = File.Exists(defaultsPath) ? ParseObject(defaultsPath) : null;
        var added = new List<string>();
        var followed = new List<string>();
        var merged = Merge((JsonObject)seed.DeepClone(), user, previous, "", added, followed);
        if (added.Count > 0 || followed.Count > 0)
        {
            File.WriteAllText(dataPath, merged.ToJsonString(PipelineConfig.Json));
        }

        if (added.Count > 0)
        {
            notes?.Add($"nouvelles clés ajoutées à {dataPath} : {string.Join(", ", added)}");
        }

        if (followed.Count > 0)
        {
            notes?.Add($"réglages restés à leur valeur par défaut, mis à la nouvelle valeur : {string.Join(", ", followed)}");
        }

        if (previous is null || !JsonNode.DeepEquals(previous, seed))
        {
            File.WriteAllText(defaultsPath, seed.ToJsonString(PipelineConfig.Json));
        }

        PipelineConfig? config;
        try
        {
            config = merged.Deserialize<PipelineConfig>(PipelineConfig.Json);
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{dataPath} : {ex.Message}");
        }

        if (config is null)
        {
            throw new ConfigException($"{dataPath} : configuration vide");
        }

        var errors = config.Validate();
        return errors.Count == 0 ? config : throw new ConfigException($"{dataPath} : " + string.Join(" ; ", errors));
    }

    private static JsonObject ParseObject(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new ConfigException($"{path} : un objet JSON est attendu");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{path} : JSON invalide ({ex.Message})");
        }
    }

    /// <summary>
    /// Overlays <paramref name="target"/> (the seed) with <paramref name="user"/>; notes the keys the
    /// user lacks, and the user values still equal to the <paramref name="previous"/> seed that the
    /// new seed changes (those take the new value).
    /// </summary>
    private static JsonObject Merge(JsonObject target, JsonObject user, JsonObject? previous, string prefix, List<string> added, List<string> followed)
    {
        foreach (var (key, value) in target.ToList())
        {
            if (!user.ContainsKey(key))
            {
                added.Add(prefix + key);
            }
            else if (value is JsonObject seedChild && user[key] is JsonObject userChild)
            {
                target[key] = Merge((JsonObject)seedChild.DeepClone(), userChild, previous?[key] as JsonObject, prefix + key + ".", added, followed);
            }
            else if (previous is not null && previous.ContainsKey(key) && JsonNode.DeepEquals(user[key], previous[key]) && !JsonNode.DeepEquals(user[key], value))
            {
                followed.Add($"{prefix}{key} ({user[key]?.ToJsonString() ?? "null"} → {value?.ToJsonString() ?? "null"})");
            }
            else
            {
                target[key] = user[key]?.DeepClone();
            }
        }

        foreach (var (key, value) in user)
        {
            if (!target.ContainsKey(key))
            {
                target[key] = value?.DeepClone();
            }
        }

        return target;
    }
}
