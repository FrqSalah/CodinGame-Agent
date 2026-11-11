using CodinGame.Core.Agents;
using CodinGame.Core.Arena;
using CodinGame.Core.CodinGameApi;
using CodinGame.Core.Config;
using CodinGame.Core.Data;
using Microsoft.Extensions.Logging;

namespace CodinGame.Core.Pipeline;

/// <summary>
/// La ferme : fait tourner les pipelines en continu, un essai à la fois, et se met
/// en pause d'elle-même quand quelque chose manque (connexion, quota, configuration).
/// </summary>
public sealed class Farm(FarmPaths paths, CodinGameClient api, IAgentRunner agents, ILogger<Farm> log)
{
    private readonly Dictionary<string, DateTime> _pausedUntil = [];
    private readonly Dictionary<string, int> _agentFailures = [];
    private readonly Dictionary<string, string> _lastStatus = [];

    public async Task RunForeverAsync(CancellationToken ct)
    {
        var names = paths.PipelineNames().ToList();
        log.LogInformation("Ferme démarrée : {N} pipeline(s) ({Noms}), données dans {Data}", names.Count, string.Join(", ", names), paths.DataRoot);
        while (!ct.IsCancellationRequested)
        {
            var ran = false;
            foreach (var name in paths.PipelineNames())
            {
                if (_pausedUntil.TryGetValue(name, out var until) && until > DateTime.UtcNow)
                {
                    continue;
                }

                try
                {
                    ran |= await RunOnceAsync(name, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (TokenRejectedException ex)
                {
                    Pause(name, TimeSpan.FromMinutes(30), $"token CodinGame : {ex.Message}", LogLevel.Critical);
                }
                catch (QuotaExhaustedException ex)
                {
                    Pause(name, ex.RetryAfter, ex.Message, LogLevel.Warning);
                }
                catch (ConfigException ex)
                {
                    Pause(name, TimeSpan.FromMinutes(1), $"configuration invalide : {ex.Message}", LogLevel.Error);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Pipeline {Pipeline} : erreur inattendue", name);
                    Pause(name, TimeSpan.FromMinutes(10), ex.Message, LogLevel.Error);
                }
            }

            if (!ran)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
    }

    /// <summary>Prépare la pipeline puis lance un essai. Renvoie false si rien n'a pu tourner.</summary>
    public async Task<bool> RunOnceAsync(string name, CancellationToken ct)
    {
        var (ctx, journal) = Open(name, closeInterrupted: true);
        using (journal)
        {
            var cfg = ctx.Config;
            if (!cfg.Boucle.Actif)
            {
                Pause(name, TimeSpan.FromMinutes(cfg.Boucle.PauseMinutesSiInactif), "en pause (boucle.actif = false)", LogLevel.Information);
                return false;
            }

            if (!await PrepareAsync(ctx, ct))
            {
                return false;
            }

            if (journal.TrialsSince(DateTime.UtcNow.Date) >= cfg.Boucle.EssaisParJour)
            {
                Pause(name, DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow, $"plafond de {cfg.Boucle.EssaisParJour} essais par jour atteint", LogLevel.Information);
                return false;
            }

            var status = await agents.CheckAsync(ct);
            if (status.Availability != AgentAvailability.Ready)
            {
                Pause(name, TimeSpan.FromMinutes(5),
                    $"{status.Detail} — connexion : docker compose exec -it farm login-antigravity", LogLevel.Warning);
                return false;
            }

            var unknown = new[] { cfg.Agents.Penseur, cfg.Agents.Codeur, cfg.Agents.RegardNeuf }
                .Select(r => r.Modele).Distinct().Where(m => status.Models.Count > 0 && !status.Models.Contains(m)).ToList();
            if (unknown.Count > 0)
            {
                Pause(name, TimeSpan.FromMinutes(2),
                    $"modèle(s) inconnu(s) d'Antigravity : {string.Join(", ", unknown)} ; disponibles : {string.Join(", ", status.Models)}", LogLevel.Error);
                return false;
            }

            var needed = 2 * cfg.Api.PartiesConfirmation;
            if (needed > 0 && await api.TestGamesLeftAsync(cfg.Jeu, ct) is { } left && left < needed)
            {
                Pause(name, TimeSpan.FromMinutes(30), $"seulement {left} parties de test CodinGame restantes (il en faut {needed})", LogLevel.Information);
                return false;
            }

            Status(name, "essai en cours", LogLevel.Information);
            var verdict = await new Trial(ctx, api, agents, log).RunAsync(ct);
            if (verdict.Verdict == Verdicts.AgentFailure)
            {
                _agentFailures[name] = _agentFailures.GetValueOrDefault(name) + 1;
                if (_agentFailures[name] >= 2)
                {
                    _agentFailures[name] = 0;
                    Pause(name, TimeSpan.FromMinutes(cfg.Boucle.PauseMinutesSiEchecAgent),
                        "deux échecs d'agent de suite (quota de l'abonnement ?)", LogLevel.Warning);
                }
            }
            else
            {
                _agentFailures[name] = 0;
            }

            return true;
        }
    }

    /// <summary>Rejuge le candidat d'un essai passé, sans clore l'essai que la boucle mène peut-être en parallèle.</summary>
    public async Task<TrialVerdict> RejudgeAsync(string name, long trialId, CancellationToken ct)
    {
        var (ctx, journal) = Open(name);
        using (journal)
        {
            return await new Trial(ctx, api, agents, log).RunAsync(ct, trialId);
        }
    }

    /// <summary>
    /// Ouvre la configuration et le journal d'une pipeline. Seule la boucle clôt les essais restés
    /// ouverts : une commande de consultation (bilan, vérification) lancée pendant un essai ne doit
    /// pas le marquer interrompu.
    /// </summary>
    public (PipelineContext Context, Journal Journal) Open(string name, bool closeInterrupted = false)
    {
        var p = paths.Pipeline(name);
        var notes = new List<string>();
        var cfg = ConfigLoader.Load(p.ConfigSeed, p.ConfigData, notes);
        foreach (var note in notes)
        {
            log.LogInformation("Pipeline {Pipeline} : {Note}", name, note);
        }

        var journal = new Journal(p.Journal);
        var closed = closeInterrupted ? journal.CloseInterruptedTrials() : 0;
        if (closed > 0)
        {
            log.LogWarning("Pipeline {Pipeline} : {N} essai(s) interrompu(s) clos", name, closed);
        }

        return (new PipelineContext(name, p, cfg, journal), journal);
    }

    /// <summary>Règles et ligue, moteur local, champion. Renvoie false si la pipeline doit attendre.</summary>
    public async Task<bool> PrepareAsync(PipelineContext ctx, CancellationToken ct)
    {
        var cfg = ctx.Config;
        var puzzle = await api.GetPuzzleAsync(cfg.Jeu, ct);
        if (!cfg.Ligues.Contains(puzzle.League, StringComparer.OrdinalIgnoreCase))
        {
            Pause(ctx.Name, TimeSpan.FromMinutes(30),
                $"le compte est en ligue {puzzle.League} ; le moteur local et le bot de cette pipeline couvrent {string.Join(", ", cfg.Ligues)}",
                LogLevel.Warning);
            return false;
        }

        if (!puzzle.Languages.Contains(cfg.Langage))
        {
            throw new ConfigException($"langage : « {cfg.Langage} » n'est pas proposé pour ce jeu");
        }

        Directory.CreateDirectory(ctx.Paths.DataDir);
        await File.WriteAllTextAsync(ctx.Paths.Rules, puzzle.Statement, ct);

        var referee = cfg.Arene.Arbitre.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (FindExecutable(referee) is null)
        {
            throw new ConfigException($"arene.arbitre : moteur « {referee} » introuvable");
        }

        await EnsureChampionAsync(ctx, ct);
        return true;
    }

    private async Task EnsureChampionAsync(PipelineContext ctx, CancellationToken ct)
    {
        var botFile = ctx.Config.Bot.Fichier;
        var championFile = ctx.Paths.ChampionFile(botFile);
        if (ctx.Journal.ChampionHash() is { } hash)
        {
            if (!File.Exists(championFile))
            {
                await File.WriteAllTextAsync(championFile, ctx.Journal.BotSource(hash)!, ct);
            }

            return;
        }

        // Premier lancement : le champion est le fichier déposé dans les données, sinon le bot de départ.
        var source = File.Exists(championFile)
            ? await File.ReadAllTextAsync(championFile, ct)
            : await File.ReadAllTextAsync(ctx.Paths.SeedBot(botFile), ct);
        var build = await ctx.Builder.BuildAsync(source, ct);
        if (!build.Success)
        {
            throw new ConfigException($"le bot de départ ne compile pas : {build.Errors}");
        }

        var newHash = BotBuilder.Hash(source);
        ctx.Journal.SaveBot(newHash, source);
        ctx.Journal.SetChampion(newHash, null);
        await File.WriteAllTextAsync(championFile, source, ct);
        log.LogInformation("Pipeline {Pipeline} : champion initial {Hash}", ctx.Name, newHash);
    }

    private static string? FindExecutable(string name)
    {
        if (Path.IsPathRooted(name) || name.Contains('/') || name.Contains('\\'))
        {
            return File.Exists(name) ? name : null;
        }

        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ""] : [""];
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .SelectMany(dir => extensions.Select(ext => Path.Combine(dir, name + ext)))
            .FirstOrDefault(File.Exists);
    }

    private void Pause(string name, TimeSpan duration, string reason, LogLevel level)
    {
        _pausedUntil[name] = DateTime.UtcNow + duration;
        Status(name, $"{reason} (nouvelle tentative dans {Math.Max(1, (int)Math.Round(duration.TotalMinutes))} min)", level);
    }

    /// <summary>N'écrit un état que s'il change, pour ne pas noyer le dashboard.</summary>
    private void Status(string name, string status, LogLevel level)
    {
        var key = status.Split(" (nouvelle tentative")[0];
        if (_lastStatus.GetValueOrDefault(name) == key)
        {
            return;
        }

        _lastStatus[name] = key;
        log.Log(level, "Pipeline {Pipeline} : {Statut}", name, status);
    }
}
