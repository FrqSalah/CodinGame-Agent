using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodinGame.Core.Config;

public sealed record AgentRole(string Outil, string Modele, int MinutesMax);

public sealed record AgentRoles(AgentRole Penseur, AgentRole Codeur, AgentRole RegardNeuf);

public sealed record BotSettings(string Fichier, string Compilation);

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
    double Beta);

public sealed record ApiSettings(int PartiesConfirmation, string Adversaire, bool SoumettreChampions);

public sealed record LoopSettings(
    bool Actif,
    int EssaisParJour,
    int RejetsAvantRegardNeuf,
    int CorrectionsCompilationMax,
    int PauseMinutesSiEchecAgent,
    int PauseMinutesSiInactif);

public sealed record ContextSettings(int EssaisResumes);

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
    ContextSettings Contexte)
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
        }

        Check(!string.IsNullOrWhiteSpace(Arene.Arbitre), "arene.arbitre", "commande du moteur obligatoire");
        Check(Arene.Parallele is >= 1 and <= 64, "arene.parallele", "entre 1 et 64");
        Check(Arene.PremierTourMs > 0 && Arene.TourMs > 0, "arene.premier_tour_ms / tour_ms", "doivent être positifs");
        Check(Arene.ToleranceTemps >= 1, "arene.tolerance_temps", "au moins 1");
        Check(Arene.PartiesMin >= 2 && Arene.PartiesMax >= Arene.PartiesMin, "arene.parties_min / parties_max", "il faut 2 ≤ min ≤ max");
        Check(Arene.Elo1 > Arene.Elo0, "arene.elo1", "doit dépasser arene.elo0");
        Check(Arene.Alpha is > 0 and < 0.5 && Arene.Beta is > 0 and < 0.5, "arene.alpha / beta", "entre 0 et 0,5");
        Check(Api.PartiesConfirmation >= 0, "api.parties_confirmation", "positif ou nul");
        Check(!string.IsNullOrWhiteSpace(Api.Adversaire), "api.adversaire", "obligatoire (« boss » ou un agent_id)");
        Check(Boucle.EssaisParJour >= 0, "boucle.essais_par_jour", "positif ou nul");
        Check(Boucle.RejetsAvantRegardNeuf >= 1, "boucle.rejets_avant_regard_neuf", "au moins 1");
        Check(Boucle.CorrectionsCompilationMax >= 0, "boucle.corrections_compilation_max", "positif ou nul");
        Check(Boucle.PauseMinutesSiEchecAgent >= 1 && Boucle.PauseMinutesSiInactif >= 1, "boucle.pause_minutes_*", "au moins 1");
        Check(Contexte.EssaisResumes >= 1, "contexte.essais_resumes", "au moins 1");
        return errors;
    }
}

public sealed class ConfigException(string message) : Exception(message);

/// <summary>
/// Charge la configuration : celle livrée avec l'application (graine), recouverte par celle
/// du dossier de données, que l'utilisateur modifie. À la première lecture, la graine est
/// copiée dans le dossier de données ; ensuite, seules les clés nouvelles y sont ajoutées.
/// </summary>
public static class ConfigLoader
{
    public static PipelineConfig Load(string seedPath, string dataPath, List<string>? notes = null)
    {
        var seed = ParseObject(seedPath);
        if (!File.Exists(dataPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
            File.WriteAllText(dataPath, seed.ToJsonString(PipelineConfig.Json));
            notes?.Add($"configuration créée : {dataPath}");
        }

        var user = ParseObject(dataPath);
        var added = new List<string>();
        var merged = Merge((JsonObject)seed.DeepClone(), user, "", added);
        if (added.Count > 0)
        {
            File.WriteAllText(dataPath, merged.ToJsonString(PipelineConfig.Json));
            notes?.Add($"nouvelles clés ajoutées à {dataPath} : {string.Join(", ", added)}");
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

    /// <summary>Recouvre <paramref name="target"/> (la graine) par <paramref name="user"/>, et note les clés absentes chez l'utilisateur.</summary>
    private static JsonObject Merge(JsonObject target, JsonObject user, string prefix, List<string> added)
    {
        foreach (var (key, value) in target.ToList())
        {
            if (!user.ContainsKey(key))
            {
                added.Add(prefix + key);
            }
            else if (value is JsonObject seedChild && user[key] is JsonObject userChild)
            {
                target[key] = Merge((JsonObject)seedChild.DeepClone(), userChild, prefix + key + ".", added);
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
