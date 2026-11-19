using System.Globalization;
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

            // Chaque rôle doit avoir au moins un modèle de sa chaîne proposé par Antigravity.
            var unusable = new[] { ("penseur", cfg.Agents.Penseur), ("codeur", cfg.Agents.Codeur), ("regard_neuf", cfg.Agents.RegardNeuf) }
                .Where(r => status.Models.Count > 0 && !r.Item2.Chain().Any(status.Models.Contains))
                .Select(r => r.Item1).ToList();
            if (unusable.Count > 0)
            {
                Pause(name, TimeSpan.FromMinutes(2),
                    $"aucun modèle utilisable pour : {string.Join(", ", unusable)} ; disponibles : {string.Join(", ", status.Models)}", LogLevel.Error);
                return false;
            }

            // The test game quota is checked when a candidate reaches the confirmation, not here:
            // most candidates never get there, and a trial should not wait for games it won't play.
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

    /// <summary>
    /// Relève le classement de chaque pipeline toutes les <c>api.releve_classement_minutes</c> et
    /// le garde au journal, pour suivre la progression dans le temps.
    /// </summary>
    public async Task RecordRankingsForeverAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = TimeSpan.FromMinutes(10);
            foreach (var name in paths.PipelineNames())
            {
                try
                {
                    var p = paths.Pipeline(name);
                    var cfg = ConfigLoader.Load(p.ConfigSeed, p.ConfigData);
                    wait = TimeSpan.FromMinutes(Math.Max(1, cfg.Api.ReleveClassementMinutes));
                    if (await api.GetRankingAsync(cfg.Jeu, ct) is { } ranking)
                    {
                        using var journal = new Journal(p.Journal);
                        journal.AddRanking(ranking);
                        log.LogInformation("Pipeline {Pipeline} : classement {Rang}/{Total}, score {Score}", name, ranking.Rank, ranking.Total, ranking.Score);
                        if (ranking.SubmissionId is { } submission && ranking.Percentage >= 100)
                        {
                            journal.SetChampionSubmissionIfMissing(submission);
                            if (journal.RecordFinalScore(submission, ranking.Score, ranking.Rank))
                            {
                                log.LogInformation("Pipeline {Pipeline} : score réel du champion {Champion} : {Score} (rang {Rang})",
                                    name, journal.ChampionHash(), ranking.Score, ranking.Rank);
                                await RollBackIfWorseAsync(name, p, cfg, journal, ct);
                            }
                        }

                        await AnalyseBattlesAsync(name, cfg, journal, ct);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Toute erreur, délai d'attente HTTP compris (TaskCanceledException), est passagère :
                    // le relevé suivant réessaie. Seul l'arrêt de la ferme termine cette boucle.
                    log.LogWarning("Pipeline {Pipeline} : relevé du classement impossible : {Erreur}", name, ex.Message);
                }
            }

            await Task.Delay(wait, ct);
        }
    }

    /// <summary>
    /// Reads the latest ranked games of the submitted champion not read yet (at most
    /// <c>analyse.parties_classees_par_releve</c>) and measures those that carry its trace line.
    /// Games without a trace are journaled too, so that they are never read again.
    /// </summary>
    private async Task AnalyseBattlesAsync(string name, PipelineConfig cfg, Journal journal, CancellationToken ct)
    {
        if (cfg.Analyse.PartiesClasseesParReleve <= 0 || journal.ChampionHash() is not { } champion)
        {
            return;
        }

        var seen = journal.AnalysedRefs("battle");
        var fresh = (await api.GetBattleGameIdsAsync(cfg.Jeu, 100, ct))
            .Where(id => !seen.Contains(id.ToString(CultureInfo.InvariantCulture)))
            .Take(cfg.Analyse.PartiesClasseesParReleve)
            .ToList();
        var traced = 0;
        foreach (var id in fresh)
        {
            var analysis = Analysis.RaceAnalysis.OfGame("battle", await api.GetGameAsync(id, ct), champion, null, null, cfg.Analyse);
            journal.AddAnalysis(analysis);
            traced += analysis.Measures is null ? 0 : 1;
        }

        if (fresh.Count > 0)
        {
            log.LogInformation("Pipeline {Pipeline} : {N} parties classées lues, {T} avec la ligne de trace", name, fresh.Count, traced);
        }
    }

    /// <summary>
    /// Restores the previous champion when the current one scores clearly lower on the real
    /// leaderboard (<see cref="RankingCheck"/>), and resubmits it. The withdrawn champion stays in
    /// the journal, marked, so the agents see it in their history.
    /// </summary>
    private async Task RollBackIfWorseAsync(string name, PipelinePaths p, PipelineConfig cfg, Journal journal, CancellationToken ct)
    {
        if (RankingCheck.Regression(journal.ScoredChampions(), journal.ChampionHash(), cfg.Api.RetourSiBaisse) is not { } r
            || journal.BotSource(r.Restore.Hash) is not { } source)
        {
            return;
        }

        journal.RetireChampion(r.Worse.Id);
        journal.SetChampion(r.Restore.Hash, r.Restore.TrialId);
        await File.WriteAllTextAsync(p.ChampionFile(cfg.Bot.Fichier), source, ct);
        log.LogWarning("Pipeline {Pipeline} : le champion {Moins} (score {S1}) fait moins bien que {Avant} (score {S2}) : retour au précédent",
            name, r.Worse.Hash, r.Worse.Score, r.Restore.Hash, r.Restore.Score);
        if (cfg.Api.SoumettreChampions)
        {
            var submission = await api.SubmitAsync(cfg.Jeu, cfg.Langage, source, ct);
            journal.SetChampionSubmission(submission);
            log.LogInformation("Pipeline {Pipeline} : champion {Champion} soumis de nouveau (soumission {Soumission})", name, r.Restore.Hash, submission);
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

    /// <summary>Juge un candidat écrit hors de la ferme, avec les mêmes étapes qu'un essai des agents.</summary>
    public async Task<TrialVerdict> JudgeGivenAsync(string name, GivenCandidate candidate, CancellationToken ct)
    {
        var (ctx, journal) = Open(name);
        using (journal)
        {
            return await new Trial(ctx, api, agents, log).RunAsync(ct, given: candidate);
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
        var repaired = closeInterrupted ? journal.RepairIdeas(Trial.IdeaOf) : 0;
        if (repaired > 0)
        {
            log.LogInformation("Pipeline {Pipeline} : {N} idée(s) vide(s) réparée(s) dans le journal", name, repaired);
        }

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

        foreach (var path in cfg.Banc.Adversaires.Where(p => !File.Exists(p)))
        {
            throw new ConfigException($"banc.adversaires : « {path} » introuvable");
        }

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
