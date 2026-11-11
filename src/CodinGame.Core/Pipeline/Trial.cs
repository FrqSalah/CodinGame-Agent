using System.Globalization;
using System.Text;
using CodinGame.Core.Agents;
using CodinGame.Core.Arena;
using CodinGame.Core.CodinGameApi;
using CodinGame.Core.Data;
using CodinGame.Core.Judging;
using Microsoft.Extensions.Logging;

namespace CodinGame.Core.Pipeline;

public static class Verdicts
{
    public const string Kept = "GARDE";
    public const string Rejected = "JETE";
    public const string Fault = "FAUTE";
    public const string NoCompile = "NON_COMPILE";
    public const string AgentFailure = "ECHEC_AGENT";
}

public sealed record TrialVerdict(string Verdict, string Reason);

/// <summary>
/// Un essai : le penseur écrit une note, le codeur l'applique, le programme compile, fait
/// jouer le candidat contre le champion dans le simulateur, puis confirme sur CodinGame.
/// Seul un calcul décide si le candidat est meilleur ; l'IA ne fait que proposer.
/// </summary>
public sealed class Trial(PipelineContext ctx, CodinGameClient api, IAgentRunner agents, ILogger log)
{
    private const string Instruction = "Lis le fichier TACHE.md de ce dossier et suis ses instructions à la lettre.";

    /// <param name="rejudge">
    /// Numéro d'un essai dont le candidat est rejugé tel quel (duel, confirmation, promotion),
    /// sans repasser par le penseur ni le codeur. Le rejugement est un nouvel essai du journal.
    /// </param>
    public async Task<TrialVerdict> RunAsync(CancellationToken ct, long? rejudge = null)
    {
        var cfg = ctx.Config;
        var journal = ctx.Journal;
        var championHash = journal.ChampionHash() ?? throw new InvalidOperationException("Aucun champion.");
        var championSource = journal.BotSource(championHash)!;
        var id = journal.StartTrial(championHash, Label(cfg.Agents.Penseur), Label(cfg.Agents.Codeur));
        Telemetry.SetTrialNumber(id);

        using var activity = Telemetry.Source.StartActivity("essai");
        activity?.SetTag("pipeline", ctx.Name);
        activity?.SetTag("essai", id);
        using var scope = log.BeginScope(new Dictionary<string, object> { ["pipeline"] = ctx.Name, ["essai"] = id });
        log.LogInformation("Essai {Essai} : début (champion {Champion})", id, championHash);

        var dir = Path.Combine(ctx.Paths.Trials, id.ToString("D4", CultureInfo.InvariantCulture));
        TrialVerdict verdict;
        DuelOutcome? duel = null;
        try
        {
            (verdict, duel) = rejudge is { } of
                ? await RejudgeStepsAsync(id, of, championHash, championSource, ct)
                : await RunStepsAsync(id, dir, championHash, championSource, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            journal.FinishTrial(id, "ERREUR", ex.Message, 0, 0, 0, null);
            throw;
        }

        journal.FinishTrial(id, verdict.Verdict, verdict.Reason, duel?.Wins ?? 0, duel?.Losses ?? 0, duel?.Draws ?? 0, duel?.Llr);
        Telemetry.Trials.Add(1, new KeyValuePair<string, object?>("verdict", verdict.Verdict));
        activity?.SetTag("verdict", verdict.Verdict);
        log.LogInformation("Essai {Essai} : {Verdict} — {Raison}", id, verdict.Verdict, verdict.Reason);

        if (rejudge is null && verdict.Verdict != Verdicts.Kept && journal.NonKeptSinceLastChange() >= cfg.Boucle.RejetsAvantRegardNeuf)
        {
            await FreshLookAsync(id, dir, championSource, ct);
        }

        return verdict;
    }

    private async Task<(TrialVerdict, DuelOutcome?)> RunStepsAsync(
        long id, string dir, string championHash, string championSource, CancellationToken ct)
    {
        var cfg = ctx.Config;
        var journal = ctx.Journal;
        var botFile = cfg.Bot.Fichier;
        var rules = ReadOrEmpty(ctx.Paths.Rules);
        var engine = ReadOrEmpty(ctx.Paths.EngineNotes);

        // 1. Penseur : une idée, écrite avant tout code.
        var thinkDir = Path.Combine(dir, "penseur");
        await WriteWorkspaceAsync(thinkDir, "penseur", ct,
            ("REGLES.md", rules),
            ("MOTEUR.md", engine),
            (botFile, championSource),
            ("HISTORIQUE.md", History(cfg.Contexte.EssaisResumes)),
            ("CONSIGNES.md", ReadOrEmpty(ctx.Paths.UserGuidance())),
            ("ORIENTATION.md", ReadOrEmpty(ctx.Paths.Orientation)));
        var think = await AskAsync(id, new AgentRequest("penseur", cfg.Agents.Penseur, thinkDir, Instruction), ct);
        if (!think.Success || string.IsNullOrWhiteSpace(think.Response))
        {
            return (new TrialVerdict(Verdicts.AgentFailure, $"penseur : {think.Error ?? "réponse vide"}"), null);
        }

        var note = think.Response.Trim();
        var idea = IdeaOf(note);
        await File.WriteAllTextAsync(Path.Combine(dir, "note.md"), note, ct);
        journal.SetTrialNote(id, idea, note);
        log.LogInformation("Essai {Essai} : idée — {Idee}", id, idea);

        // 2. Codeur : applique la note, et rien d'autre.
        var codeDir = Path.Combine(dir, "codeur");
        await WriteWorkspaceAsync(codeDir, "codeur", ct,
            ("NOTE.md", note),
            ("REGLES.md", rules),
            ("MOTEUR.md", engine),
            (botFile, championSource));
        var code = await agents.RunAsync(new AgentRequest("codeur", cfg.Agents.Codeur, codeDir, Instruction), ct);
        journal.AddTrialTokens(id, code.Tokens);
        var candidate = ReadOrEmpty(Path.Combine(codeDir, botFile));
        if (!code.Success)
        {
            return (new TrialVerdict(Verdicts.AgentFailure, $"codeur : {code.Error}"), null);
        }

        if (string.IsNullOrWhiteSpace(candidate) || Normalize(candidate) == Normalize(championSource))
        {
            return (new TrialVerdict(Verdicts.AgentFailure, "codeur : le bot n'a pas été modifié"), null);
        }

        // 3. Compilation, avec quelques retours au codeur si besoin.
        var corrections = 0;
        var build = await ctx.Builder.BuildAsync(candidate, ct);
        while (!build.Success && corrections < cfg.Boucle.CorrectionsCompilationMax)
        {
            corrections++;
            log.LogInformation("Essai {Essai} : erreur de compilation, correction {N}", id, corrections);
            await File.WriteAllTextAsync(Path.Combine(codeDir, "ERREURS.md"), "```\n" + Clip(build.Errors, 6000) + "\n```\n", ct);
            await agents.GrantAsync(codeDir, ct);
            var fix = await agents.RunAsync(new AgentRequest("codeur", cfg.Agents.Codeur, codeDir,
                $"Lis TACHE.md puis ERREURS.md : {botFile} ne compile pas. Corrige-le sans changer l'idée de NOTE.md."), ct);
            journal.AddTrialTokens(id, fix.Tokens);
            candidate = ReadOrEmpty(Path.Combine(codeDir, botFile));
            build = await ctx.Builder.BuildAsync(candidate, ct);
        }

        var hash = BotBuilder.Hash(candidate);
        journal.SaveBot(hash, candidate);
        journal.SetTrialCandidate(id, hash, corrections);
        await File.WriteAllTextAsync(Path.Combine(dir, "candidat" + Path.GetExtension(botFile)), candidate, ct);
        if (!build.Success)
        {
            return (new TrialVerdict(Verdicts.NoCompile, Clip(build.Errors, 300)), null);
        }

        return await JudgeAsync(id, hash, candidate, build.Executable!, championHash, championSource, ct);
    }

    /// <summary>Rejuge le candidat d'un essai passé contre le champion actuel.</summary>
    private async Task<(TrialVerdict, DuelOutcome?)> RejudgeStepsAsync(
        long id, long of, string championHash, string championSource, CancellationToken ct)
    {
        var journal = ctx.Journal;
        var (hash, idea, note) = journal.TrialCandidate(of);
        var candidate = hash is null ? null : journal.BotSource(hash);
        if (candidate is null)
        {
            throw new InvalidOperationException($"L'essai {of} n'a pas de candidat dans le journal.");
        }

        journal.SetTrialNote(id, $"Rejugement de l'essai {of} : {idea}", note ?? "");
        journal.SetTrialCandidate(id, hash!, 0);
        log.LogInformation("Essai {Essai} : rejugement du candidat {Hash} de l'essai {Of}", id, hash, of);
        if (hash == championHash)
        {
            return (new TrialVerdict(Verdicts.Rejected, "ce candidat est déjà le champion"), null);
        }

        var build = await ctx.Builder.BuildAsync(candidate, ct);
        return build.Success
            ? await JudgeAsync(id, hash!, candidate, build.Executable!, championHash, championSource, ct)
            : (new TrialVerdict(Verdicts.NoCompile, Clip(build.Errors, 300)), null);
    }

    /// <summary>
    /// Duel local jusqu'au verdict du test séquentiel, confirmation sur CodinGame, puis promotion
    /// et soumission. Seul ce calcul décide si un candidat devient champion.
    /// </summary>
    private async Task<(TrialVerdict, DuelOutcome?)> JudgeAsync(
        long id, string hash, string candidate, string candidateExe, string championHash, string championSource, CancellationToken ct)
    {
        var cfg = ctx.Config;
        var journal = ctx.Journal;
        var botFile = cfg.Bot.Fichier;

        // 4. Duel dans le simulateur, jusqu'à ce que le test séquentiel tranche.
        var championBuild = await ctx.Builder.BuildAsync(championSource, ct);
        if (!championBuild.Success)
        {
            throw new InvalidOperationException($"Le champion ne compile plus : {Clip(championBuild.Errors, 300)}");
        }

        DuelOutcome duel;
        using (Telemetry.Source.StartActivity("duel"))
        {
            duel = await new Duel(ctx.Referee, cfg.Arene, log).RunAsync(candidateExe, championBuild.Executable!,
                (seed, seat, r) => journal.AddLocalGame(id, seed, seat, ResultFor(r, seat),
                    r.Faults[seat] == FaultKind.None ? null : r.Detail),
                ct);
        }

        if (duel.CandidateFault is not null)
        {
            return (new TrialVerdict(Verdicts.Fault, $"simulateur : {duel.CandidateFault}"), duel);
        }

        var score = $"{duel.Wins} V / {duel.Losses} D / {duel.Draws} N contre le champion, LLR {duel.Llr.ToString("F2", CultureInfo.InvariantCulture)}";
        if (duel.Decision != SprtDecision.Better)
        {
            return (new TrialVerdict(Verdicts.Rejected, $"pas d'amélioration mesurée : {score}"), duel);
        }

        // 5. Confirmation sur CodinGame : le vrai moteur, le vrai compilateur, les vraies limites de temps.
        //    Le candidat rejoue exactement les circuits des parties de confirmation du champion.
        var k = cfg.Api.PartiesConfirmation;
        if (k > 0)
        {
            using (Telemetry.Source.StartActivity("confirmation"))
            {
                var known = journal.ApiGames(championHash, cfg.Api.Adversaire).Take(k).ToList();
                var championGames = known.Concat(await PlayApiGamesAsync(championHash, championSource,
                    Enumerable.Repeat<string?>(null, k - known.Count).ToList(), null, ct)).ToList();
                var candidateGames = await PlayApiGamesAsync(hash, candidate, championGames.Select(g => (string?)g.Options).ToList(), id, ct);
                var faults = candidateGames.Count(g => g.Fault);
                var candWins = candidateGames.Count(g => g.Outcome == nameof(Outcome.Win));
                var champWins = championGames.Count(g => g.Outcome == nameof(Outcome.Win));
                log.LogInformation("Essai {Essai} : sur CodinGame contre {Adversaire}, candidat {Cand}/{N}, champion {Champ}/{N}",
                    id, cfg.Api.Adversaire, candWins, k, champWins, k);
                if (faults > 0)
                {
                    return (new TrialVerdict(Verdicts.Fault, $"CodinGame : {faults} partie(s) en faute (plantage, temps ou coup invalide)"), duel);
                }

                Telemetry.SetChampionScore(candWins, candidateGames.Count);
                score += $" ; sur CodinGame contre {cfg.Api.Adversaire} : {candWins}/{k} (champion {champWins}/{k})";
            }
        }

        journal.SetChampion(hash, id);
        await File.WriteAllTextAsync(ctx.Paths.ChampionFile(botFile), candidate, ct);
        if (cfg.Api.SoumettreChampions)
        {
            var submission = await api.SubmitAsync(cfg.Jeu, cfg.Langage, candidate, ct);
            log.LogInformation("Essai {Essai} : nouveau champion soumis au classement (soumission {Soumission})", id, submission);
            score += $" ; soumis au classement ({submission})";
        }

        return (new TrialVerdict(Verdicts.Kept, score), duel);
    }

    /// <summary>Joue des parties de test sur CodinGame : une par option donnée (null = circuit tiré au hasard).</summary>
    private async Task<List<ApiGameEntry>> PlayApiGamesAsync(
        string hash, string source, IReadOnlyList<string?> options, long? trialId, CancellationToken ct)
    {
        var opponent = ctx.Config.Api.Adversaire;
        var games = new List<ApiGameEntry>();
        foreach (var option in options)
        {
            var result = await api.PlayTestGameAsync(ctx.Config.Jeu, ctx.Config.Langage, source, opponent, option, ct);
            if (result.Log is null)
            {
                log.LogWarning("Partie de test non jouée ({Failure}) : nouvel essai dans une minute", result.Failure);
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                result = await api.PlayTestGameAsync(ctx.Config.Jeu, ctx.Config.Langage, source, opponent, option, ct);
            }

            if (result.Log is null)
            {
                throw new CodinGameException($"Partie de test impossible : {result.Failure}");
            }

            var entry = new ApiGameEntry(result.Log.GameId, hash, opponent, result.Log.Options,
                result.Log.OutcomeFor(0).ToString(), result.Log.TestedCodeFaulted);
            ctx.Journal.AddApiGame(entry.GameId, hash, opponent, entry.Options, entry.Outcome, entry.Fault, trialId);
            games.Add(entry);
        }

        return games;
    }

    /// <summary>Après plusieurs rejets de suite : un autre modèle relit tout et propose une autre direction.</summary>
    private async Task FreshLookAsync(long id, string dir, string championSource, CancellationToken ct)
    {
        var cfg = ctx.Config;
        log.LogInformation("Essai {Essai} : {N} essais sans promotion, appel du regard neuf", id, cfg.Boucle.RejetsAvantRegardNeuf);
        var lookDir = Path.Combine(dir, "regard-neuf");
        await WriteWorkspaceAsync(lookDir, "regard_neuf", ct,
            ("REGLES.md", ReadOrEmpty(ctx.Paths.Rules)),
            ("MOTEUR.md", ReadOrEmpty(ctx.Paths.EngineNotes)),
            (cfg.Bot.Fichier, championSource),
            ("HISTORIQUE.md", History(int.MaxValue)),
            ("CONSIGNES.md", ReadOrEmpty(ctx.Paths.UserGuidance())),
            ("ORIENTATION_PRECEDENTE.md", ReadOrEmpty(ctx.Paths.Orientation)));
        var look = await AskAsync(id, new AgentRequest("regard_neuf", cfg.Agents.RegardNeuf, lookDir, Instruction), ct);
        if (look.Success && !string.IsNullOrWhiteSpace(look.Response))
        {
            await File.WriteAllTextAsync(ctx.Paths.Orientation, look.Response.Trim(), ct);
            ctx.Journal.AddOrientation(id, look.Response.Trim());
            log.LogInformation("Essai {Essai} : nouvelle orientation écrite", id);
        }
        else
        {
            log.LogWarning("Essai {Essai} : le regard neuf a échoué : {Erreur}", id, look.Error);
        }
    }

    /// <summary>
    /// Appel d'un agent dont la réponse est le produit (penseur, regard neuf). Antigravity rend
    /// parfois un succès sans aucun texte : on relance alors, dans la limite de la configuration.
    /// </summary>
    private async Task<AgentResult> AskAsync(long id, AgentRequest request, CancellationToken ct)
    {
        var result = await agents.RunAsync(request, ct);
        ctx.Journal.AddTrialTokens(id, result.Tokens);
        for (var retry = 0; retry < ctx.Config.Boucle.RelancesReponseVide
             && result.Success && string.IsNullOrWhiteSpace(result.Response); retry++)
        {
            log.LogWarning("Essai {Essai} : réponse vide du {Role}, relance {N} — sortie de l'outil : {Diagnostic}",
                id, request.Role, retry + 1, result.Diagnostic);
            result = await agents.RunAsync(request, ct);
            ctx.Journal.AddTrialTokens(id, result.Tokens);
        }

        if (result.Success && string.IsNullOrWhiteSpace(result.Response))
        {
            log.LogWarning("Essai {Essai} : réponse encore vide du {Role} — sortie de l'outil : {Diagnostic}", id, request.Role, result.Diagnostic);
        }

        return result;
    }

    private async Task WriteWorkspaceAsync(string dir, string role, CancellationToken ct, params (string Name, string Content)[] files)
    {
        Directory.CreateDirectory(dir);
        var task = ctx.Fill(await File.ReadAllTextAsync(ctx.Paths.Prompt(role), ct));
        await File.WriteAllTextAsync(Path.Combine(dir, "TACHE.md"), task, ct);
        foreach (var (name, content) in files)
        {
            if (!string.IsNullOrWhiteSpace(content))
            {
                await File.WriteAllTextAsync(Path.Combine(dir, name), content, ct);
            }
        }

        await agents.GrantAsync(dir, ct);
    }

    /// <summary>Résumé du journal donné aux agents : les derniers essais, puis toutes les idées déjà jetées.</summary>
    private string History(int recent)
    {
        var all = ctx.Journal.AllTrials();
        var sb = new StringBuilder("# Historique des essais\n\n");
        var champions = ctx.Journal.ChampionCount();
        sb.Append(CultureInfo.InvariantCulture, $"Promotions depuis le bot de départ : {champions - 1}. Essais terminés : {all.Count}.\n\n");
        if (all.Count == 0)
        {
            return sb.Append("Aucun essai pour l'instant : c'est le premier.\n").ToString();
        }

        sb.Append("## Derniers essais (le plus récent en premier)\n\n| # | Verdict | Idée | Contre le champion | Raison |\n|---|---|---|---|---|\n");
        foreach (var t in all.TakeLast(Math.Min(recent, all.Count)).Reverse())
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"| {t.Id} | {t.Verdict} | {Cell(t.Idea)} | {t.Wins} V / {t.Losses} D | {Cell(t.Reason)} |\n");
        }

        var rejected = all.Where(t => t.Verdict is not Verdicts.Kept and not null && t.Idea is not null).ToList();
        if (rejected.Count > 0)
        {
            sb.Append("\n## Idées déjà essayées sans succès\n\n");
            foreach (var t in rejected)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- #{t.Id} ({t.Verdict}) : {Cell(t.Idea)}\n");
            }
        }

        return sb.ToString();
    }

    private static string IdeaOf(string note)
    {
        foreach (var line in note.Split('\n'))
        {
            var l = line.Trim().TrimStart('#', '*', '-', ' ');
            if (l.StartsWith("IDEE", StringComparison.OrdinalIgnoreCase) || l.StartsWith("IDÉE", StringComparison.OrdinalIgnoreCase))
            {
                var value = l[4..].TrimStart(':', ' ', '*', '\t');
                if (value.Length > 0)
                {
                    return Clip(value, 200);
                }
            }
        }

        return Clip(note.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "", 200);
    }

    private static string ResultFor(MatchResult r, int seat) =>
        r.IsDraw ? "egalite" : r.Winner == seat ? "victoire" : "defaite";

    private static string Label(Config.AgentRole r) => $"{r.Outil}/{r.Modele}";

    private static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();

    private static string Cell(string? s) => Clip((s ?? "").Replace('\n', ' ').Replace("|", "/"), 160);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
