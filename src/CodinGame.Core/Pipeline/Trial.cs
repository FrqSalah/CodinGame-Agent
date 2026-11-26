using System.Globalization;
using System.Text;
using CodinGame.Core.Agents;
using CodinGame.Core.Analysis;
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

    /// <summary>The thinker's note was unusable twice (no idea, no target, too long): no code was written.</summary>
    public const string BadNote = "NOTE_REFUSEE";
}

public sealed record TrialVerdict(string Verdict, string Reason);

/// <summary>Un candidat écrit hors de la ferme : son source, son idée en une ligne et sa note.</summary>
public sealed record GivenCandidate(string Source, string Idea, string Note);

/// <summary>
/// Un essai : le penseur écrit une note, le codeur l'applique, le programme compile, fait
/// jouer le candidat contre le champion dans le simulateur, puis confirme sur CodinGame.
/// Seul un calcul décide si le candidat est meilleur ; l'IA ne fait que proposer.
/// </summary>
public sealed class Trial(PipelineContext ctx, CodinGameClient api, IAgentRunner agents, ILogger log)
{
    private const string Instruction = "Read the file TASK.md in this folder and follow its instructions to the letter.";

    /// <summary>
    /// Taille maximale d'une consigne passée en argument. Linux refuse un argument de plus de
    /// 128 Kio : au-delà de cette marge, l'agent lit ses fichiers lui-même, comme avant.
    /// </summary>
    private const int MaxInlinePromptBytes = 120_000;

    /// <param name="rejudge">
    /// Numéro d'un essai dont le candidat est rejugé tel quel (duel, confirmation, promotion),
    /// sans repasser par le penseur ni le codeur. Le rejugement est un nouvel essai du journal.
    /// </param>
    /// <param name="given">Candidat fourni de l'extérieur (commande <c>juger</c>), jugé de la même façon.</param>
    /// <param name="resume">
    /// An open trial that a stop interrupted (<see cref="Journal.ResumableTrial"/>): its candidate is
    /// judged again from the duel games already journaled, under the same trial number.
    /// </param>
    public async Task<TrialVerdict> RunAsync(CancellationToken ct, long? rejudge = null, GivenCandidate? given = null, long? resume = null)
    {
        var cfg = ctx.Config;
        var journal = ctx.Journal;
        var championHash = journal.ChampionHash() ?? throw new InvalidOperationException("Aucun champion.");
        var championSource = journal.BotSource(championHash)!;
        var id = resume ?? journal.StartTrial(championHash, Label(cfg.Agents.Penseur), Label(cfg.Agents.Codeur));
        Telemetry.SetTrialNumber(id);

        using var activity = Telemetry.Source.StartActivity("essai");
        activity?.SetTag("pipeline", ctx.Name);
        activity?.SetTag("essai", id);
        using var scope = log.BeginScope(new Dictionary<string, object> { ["pipeline"] = ctx.Name, ["essai"] = id });
        log.LogInformation("Essai {Essai} : {Debut} (champion {Champion})", id, resume is null ? "début" : "reprise après un arrêt", championHash);

        var dir = Path.Combine(ctx.Paths.Trials, id.ToString("D4", CultureInfo.InvariantCulture));
        TrialVerdict verdict;
        DuelOutcome? duel = null;
        try
        {
            (verdict, duel) = given is not null
                ? await JudgeGivenAsync(id, given, championHash, championSource, ct)
                : rejudge is { } of
                    ? await RejudgeStepsAsync(id, of, championHash, championSource, ct)
                    : resume is not null
                        ? await ResumeStepsAsync(id, championHash, championSource, ct)
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

        if (rejudge is null && given is null && verdict.Verdict != Verdicts.Kept && journal.NonKeptSinceLastChange() >= cfg.Boucle.RejetsAvantRegardNeuf)
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
        var thinkPrompt = await WriteWorkspaceAsync(thinkDir, "penseur", ct,
            ("RULES.md", rules),
            ("ENGINE.md", engine),
            ("STRATEGY.md", ReadOrEmpty(ctx.Paths.Strategy)),
            (botFile, championSource),
            ("HISTORY.md", History(cfg.Contexte.EssaisResumes)),
            ("LOSSES.md", Losses(championHash)),
            ("GUIDANCE.md", ReadOrEmpty(ctx.Paths.UserGuidance())),
            ("ORIENTATION.md", ReadOrEmpty(ctx.Paths.Orientation)));
        var thinkRequest = new AgentRequest("penseur", cfg.Agents.Penseur, thinkDir, thinkPrompt, TimeSpan.FromMinutes(cfg.Agents.BlocageMinutes));
        var think = await AskAsync(id, thinkRequest, ct);
        if (!think.Success || string.IsNullOrWhiteSpace(think.Response))
        {
            return (new TrialVerdict(Verdicts.AgentFailure, $"penseur : {think.Error ?? "réponse vide"}"), null);
        }

        // A note the coder could not use is asked again once, with what is wrong in it.
        var thinkTask = await File.ReadAllTextAsync(Path.Combine(thinkDir, "TASK.md"), ct);
        var note = AnswerOf(think.Response.Trim());
        var problems = NoteProblems(note, thinkTask, cfg.Boucle.MotsNoteMax);
        if (problems.Count > 0)
        {
            log.LogWarning("Essai {Essai} : note du penseur refusée ({Problemes}), nouvelle demande", id, string.Join(" ; ", problems));
            await File.WriteAllTextAsync(Path.Combine(dir, "note-refusee.md"), think.Response, ct);
            think = await AskAsync(id, thinkRequest with { Prompt = thinkPrompt + Refusal(problems) }, ct);
            if (!think.Success || string.IsNullOrWhiteSpace(think.Response))
            {
                return (new TrialVerdict(Verdicts.AgentFailure, $"penseur : {think.Error ?? "réponse vide"}"), null);
            }

            note = AnswerOf(think.Response.Trim());
            problems = NoteProblems(note, thinkTask, cfg.Boucle.MotsNoteMax);
        }

        var idea = IdeaOf(note);
        await File.WriteAllTextAsync(Path.Combine(dir, "note.md"), note, ct);
        journal.SetTrialNote(id, idea, note);
        journal.SetTrialTarget(id, TargetOf(note));
        log.LogInformation("Essai {Essai} : idée — {Idee}", id, idea);
        if (problems.Count > 0)
        {
            return (new TrialVerdict(Verdicts.BadNote, $"note du penseur refusée deux fois : {string.Join(" ; ", problems)}"), null);
        }

        // 2. Codeur : applique la note, et rien d'autre.
        var codeDir = Path.Combine(dir, "codeur");
        var codePrompt = await WriteWorkspaceAsync(codeDir, "codeur", ct,
            ("NOTE.md", note),
            ("ENGINE.md", engine),
            (botFile, championSource));
        var code = await agents.RunAsync(new AgentRequest("codeur", cfg.Agents.Codeur, codeDir, codePrompt, TimeSpan.FromMinutes(cfg.Agents.BlocageMinutes)), ct);
        journal.AddTrialTokens(id, "codeur", code.Tokens);
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
            var fixPrompt = await WriteWorkspaceAsync(codeDir, "codeur", ct,
                ("NOTE.md", note),
                ("ENGINE.md", engine),
                (botFile, candidate),
                ("ERRORS.md", Clip(build.Errors, 6000)));
            var fix = await agents.RunAsync(new AgentRequest("codeur", cfg.Agents.Codeur, codeDir, fixPrompt, TimeSpan.FromMinutes(cfg.Agents.BlocageMinutes)), ct);
            journal.AddTrialTokens(id, "codeur", fix.Tokens);
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

        // 4. Lab: quick games against the champion. A fault or a clear deficit
        //    goes back to the coder, who can fix a bug before the expensive judge. The judge alone decides.
        var lab = cfg.Laboratoire;
        for (var round = 0; lab.Parties > 0; round++)
        {
            var (outcome, measures) = await LabAsync(build.Executable!, championSource, ct);
            var rate = outcome.Games == 0 ? 0 : (outcome.Wins + 0.5 * outcome.Draws) / outcome.Games;
            log.LogInformation("Essai {Essai} : laboratoire {Tour} — {V} V / {D} D{Faute}", id, round + 1, outcome.Wins, outcome.Losses,
                outcome.CandidateFault is null ? "" : $", faute : {outcome.CandidateFault}");
            if (round >= lab.RevisionsMax || (outcome.CandidateFault is null && rate >= lab.SeuilRevision))
            {
                break;
            }

            var revisePrompt = await WriteWorkspaceAsync(codeDir, "codeur", ct,
                ("NOTE.md", note),
                ("ENGINE.md", engine),
                (botFile, candidate),
                ("LAB.md", LabReport(outcome, rate, LossReport.Comparison(measures))));
            var revision = await agents.RunAsync(new AgentRequest("codeur", cfg.Agents.Codeur, codeDir, revisePrompt, TimeSpan.FromMinutes(cfg.Agents.BlocageMinutes)), ct);
            journal.AddTrialTokens(id, "codeur", revision.Tokens);
            var revised = ReadOrEmpty(Path.Combine(codeDir, botFile));
            if (!revision.Success || string.IsNullOrWhiteSpace(revised) || Normalize(revised) == Normalize(candidate))
            {
                log.LogInformation("Essai {Essai} : le codeur garde sa version", id);
                break;
            }

            var revisedBuild = await ctx.Builder.BuildAsync(revised, ct);
            if (!revisedBuild.Success)
            {
                log.LogInformation("Essai {Essai} : la version corrigée ne compile pas, la précédente est gardée", id);
                break;
            }

            candidate = revised;
            build = revisedBuild;
            hash = BotBuilder.Hash(candidate);
            journal.SaveBot(hash, candidate);
            journal.SetTrialCandidate(id, hash, corrections);
            await File.WriteAllTextAsync(Path.Combine(dir, "candidat" + Path.GetExtension(botFile)), candidate, ct);
        }

        return await JudgeAsync(id, hash, candidate, build.Executable!, championHash, championSource, ct);
    }

    /// <summary>Quick lab duel: at most <c>laboratoire.parties</c> games, never extended, nothing journaled.</summary>
    private async Task<(DuelOutcome Outcome, List<RaceMeasures> Measures)> LabAsync(string candidateExe, string championSource, CancellationToken ct)
    {
        var champion = await ctx.Builder.BuildAsync(championSource, ct);
        var n = ctx.Config.Laboratoire.Parties;
        var settings = ctx.Config.Arene with
        {
            PartiesMax = n,
            PartiesMin = Math.Min(ctx.Config.Arene.PartiesMin, n),
            PartiesProlongation = n,
        };
        var measures = new List<RaceMeasures>();
        using (Telemetry.Source.StartActivity("laboratoire"))
        {
            var outcome = await new Duel(ctx.Referee, settings, log).RunAsync(candidateExe, champion.Executable!,
                (seed, seat, r) =>
                {
                    if (RaceAnalysis.OfLocalGame(r, seat, seed, 0, "", "", ctx.Config.Analyse)?.Measures is { } m)
                    {
                        measures.Add(m);
                    }
                },
                ct, relay: true);
            return (outcome, measures);
        }
    }

    /// <summary>What the coder learns from the lab, in the language of its prompt.</summary>
    public static string LabReport(DuelOutcome o, double rate, string? comparison = null)
    {
        var sb = new StringBuilder("# Lab results of your previous version\n\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"Quick games against the current champion: {o.Wins} wins, {o.Losses} losses, {o.Draws} draws ({rate:P0} win rate).\n\n");
        if (comparison is not null)
        {
            sb.Append("Measured, your version vs the champion, per game: ").Append(comparison).Append(".\n\n");
        }

        if (o.CandidateFault is not null)
        {
            sb.Append("Your version FAILED a game (crash, timeout or invalid output): ").Append(o.CandidateFault).Append("\n\n");
        }

        sb.Append("A version that loses clearly more than it wins against the champion usually hides a bug ")
          .Append("in the implementation (sign, unit, index, time limit). Look for one and fix it, keeping the ")
          .Append("idea of NOTE.md. If you find no bug, write the file unchanged: the judge will decide.\n");
        return sb.ToString();
    }

    /// <summary>
    /// Rejuge le candidat d'un essai passé contre le champion actuel. Against the same champion, the
    /// duel goes on from that trial's games: the same pair on the same seeds, already measured.
    /// </summary>
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

        var played = journal.TrialChampion(of) == championHash ? journal.LocalGames(of) : null;
        return await JudgeGivenAsync(id, new GivenCandidate(candidate, $"Rejugement de l'essai {of} : {idea}", note ?? ""), championHash, championSource, ct, played);
    }

    /// <summary>Judges again the candidate of an interrupted trial, from the duel games it already played.</summary>
    private async Task<(TrialVerdict, DuelOutcome?)> ResumeStepsAsync(long id, string championHash, string championSource, CancellationToken ct)
    {
        var hash = ctx.Journal.TrialCandidate(id).Hash ?? throw new InvalidOperationException($"L'essai {id} n'a pas de candidat dans le journal.");
        var candidate = ctx.Journal.BotSource(hash) ?? throw new InvalidOperationException($"Le source du candidat {hash} manque au journal.");
        var build = await ctx.Builder.BuildAsync(candidate, ct);
        return build.Success
            ? await JudgeAsync(id, hash, candidate, build.Executable!, championHash, championSource, ct, ctx.Journal.LocalGames(id))
            : (new TrialVerdict(Verdicts.NoCompile, Clip(build.Errors, 300)), null);
    }

    /// <summary>Juge un candidat fourni tel quel : mêmes étapes et mêmes seuils que pour un candidat des agents.</summary>
    private async Task<(TrialVerdict, DuelOutcome?)> JudgeGivenAsync(
        long id, GivenCandidate given, string championHash, string championSource, CancellationToken ct,
        IReadOnlyList<PlayedGame>? played = null)
    {
        var journal = ctx.Journal;
        var hash = BotBuilder.Hash(given.Source);
        journal.SaveBot(hash, given.Source);
        journal.SetTrialNote(id, given.Idea, given.Note);
        journal.SetTrialCandidate(id, hash, 0);
        log.LogInformation("Essai {Essai} : candidat fourni {Hash} — {Idee}", id, hash, given.Idea);
        if (hash == championHash)
        {
            return (new TrialVerdict(Verdicts.Rejected, "ce candidat est déjà le champion"), null);
        }

        var build = await ctx.Builder.BuildAsync(given.Source, ct);
        return build.Success
            ? await JudgeAsync(id, hash, given.Source, build.Executable!, championHash, championSource, ct, played)
            : (new TrialVerdict(Verdicts.NoCompile, Clip(build.Errors, 300)), null);
    }

    /// <summary>
    /// Duel local jusqu'au verdict du test séquentiel, confirmation sur CodinGame, puis promotion
    /// et soumission. Seul ce calcul décide si un candidat devient champion.
    /// </summary>
    private async Task<(TrialVerdict, DuelOutcome?)> JudgeAsync(
        long id, string hash, string candidate, string candidateExe, string championHash, string championSource, CancellationToken ct,
        IReadOnlyList<PlayedGame>? played = null)
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
                (seed, seat, r) =>
                {
                    journal.AddLocalGame(id, seed, seat, Results.For(r, seat), r.Faults[seat] == FaultKind.None ? null : r.Detail);
                    if (RaceAnalysis.OfLocalGame(r, seat, seed, id, hash, championHash, cfg.Analyse) is { } analysis)
                    {
                        journal.AddAnalysis(analysis);
                    }
                },
                ct, relay: true, played);
        }

        // What the duel measured, candidate against champion: the agents see it in the history.
        if (LossReport.Comparison(journal.TrialAnalyses(id).Select(a => a.Measures!).ToList()) is { } comparison)
        {
            journal.SetTrialMeasures(id, comparison);
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

        if (ChampionChanged(championHash, score) is { } changed)
        {
            return (changed, duel);
        }

        // 5. Bench: beating the champion is not enough. Race by race, the candidate must not do clearly
        //    worse than the champion against other bots: outside ones and the former champions with the
        //    best real scores (banc.anciens_champions), so that an idea that only exploits the latest
        //    champion does not pass.
        var benchExes = cfg.Banc.Adversaires.Count > 0 || cfg.Banc.AnciensChampions > 0
            ? await ctx.BuildBenchOpponentsAsync(championHash, ct)
            : new Dictionary<string, string>();
        if (benchExes.Count > 0)
        {
            using (Telemetry.Source.StartActivity("banc"))
            {
                var (bench, fault) = await RunBenchAsync(hash, candidateExe, championHash, championBuild.Executable!, benchExes, ct);
                if (fault is not null)
                {
                    return (new TrialVerdict(Verdicts.Fault, $"banc : {fault}"), duel);
                }

                var n = bench.Cases;
                var line = $"banc ({string.Join(", ", benchExes.Keys)}) : candidat {bench.CandidateWins}/{n}, champion {bench.ChampionWins}/{n} (+{bench.Plus} / -{bench.Minus})";
                log.LogInformation("Essai {Essai} : {Banc}", id, line);
                if (Confirmation.ClearlyWorse(bench.Plus, bench.Minus, cfg.Banc.Alpha))
                {
                    var chance = Confirmation.SignTest(bench.Plus, bench.Minus).ToString("P0", CultureInfo.InvariantCulture);
                    return (new TrialVerdict(Verdicts.Rejected,
                        $"meilleur que le champion mais nettement moins bon que lui contre le banc (hasard : {chance}) : {score} ; {line}"), duel);
                }

                score += $" ; {line}";
                Telemetry.SetBenchWinRate((double)bench.CandidateWins / n);
            }
        }

        // 6. Confirmation sur CodinGame : le vrai moteur, le vrai compilateur, les vraies limites de temps,
        //    contre de vrais joueurs classés juste au-dessus de nous (api.confirmation_joueurs). Le candidat
        //    rejoue exactement les circuits du champion contre chaque adversaire. It is rejected on a
        //    fault, or when it does clearly worse than the champion (Confirmation.ClearlyWorse).
        var k = cfg.Api.PartiesConfirmation;
        if (k > 0)
        {
            using (Telemetry.Source.StartActivity("confirmation"))
            {
                await WaitForTestGamesAsync(cfg.Api.ConfirmationCost, ct);
                var opponents = await ConfirmationOpponentsAsync(ct);
                var championGames = new List<ApiGameEntry>();
                var candidateGames = new List<ApiGameEntry>();
                foreach (var opponent in opponents)
                {
                    var known = journal.ApiGames(championHash, opponent).Take(k).ToList();
                    var champion = known.Concat(await PlayApiGamesAsync(championHash, championSource, opponent,
                        Enumerable.Repeat<string?>(null, k - known.Count).ToList(), null, ct)).ToList();
                    championGames.AddRange(champion);
                    candidateGames.AddRange(await PlayApiGamesAsync(hash, candidate, opponent, champion.Select(g => (string?)g.Options).ToList(), id, ct));
                }

                var faults = candidateGames.Count(g => g.Fault);
                var (plus, minus, candWins, champWins) = Confirmation.Compare(championGames, candidateGames);
                var n = candidateGames.Count;
                var who = cfg.Api.ConfirmationJoueurs > 0 ? $"{opponents.Count} joueurs classés au-dessus" : cfg.Api.Adversaire;
                var line = $"sur CodinGame contre {who} : candidat {candWins}/{n}, champion {champWins}/{n} (+{plus} / -{minus})";
                log.LogInformation("Essai {Essai} : {Confirmation}", id, line);
                if (faults > 0)
                {
                    return (new TrialVerdict(Verdicts.Fault, $"CodinGame : {faults} partie(s) en faute (plantage, temps ou coup invalide)"), duel);
                }

                if (cfg.Api.ConfirmationJoueurs > 0 && Confirmation.ClearlyWorse(plus, minus, cfg.Api.ConfirmationAlpha))
                {
                    var chance = Confirmation.SignTest(plus, minus).ToString("P0", CultureInfo.InvariantCulture);
                    return (new TrialVerdict(Verdicts.Rejected,
                        $"meilleur que le champion en local mais nettement moins bon que lui contre de vrais joueurs (hasard : {chance}) : {score} ; {line}"), duel);
                }

                Telemetry.SetChampionScore(candWins, n);
                score += $" ; {line}";
            }
        }

        if (ChampionChanged(championHash, score) is { } late)
        {
            return (late, duel);
        }

        journal.SetChampion(hash, id);
        await File.WriteAllTextAsync(ctx.Paths.ChampionFile(botFile), candidate, ct);
        if (cfg.Api.SoumettreChampions)
        {
            var submission = await api.SubmitAsync(cfg.Jeu, cfg.Langage, candidate, ct);
            journal.SetChampionSubmission(submission);
            log.LogInformation("Essai {Essai} : nouveau champion soumis au classement (soumission {Soumission})", id, submission);
            score += $" ; soumis au classement ({submission})";
        }

        return (new TrialVerdict(Verdicts.Kept, score), duel);
    }

    /// <summary>
    /// A candidate is promoted only over the champion it was measured against. The champion can change
    /// during a trial (a rollback of the ranking loop, a rejudge kept meanwhile): beating the former one
    /// says nothing about the new one, so the candidate is rejected, to be rejudged against it.
    /// </summary>
    private TrialVerdict? ChampionChanged(string measuredAgainst, string score) =>
        ctx.Journal.ChampionHash() is { } now && now != measuredAgainst
            ? new TrialVerdict(Verdicts.Rejected,
                $"meilleur que le champion {measuredAgainst}, mais le champion est devenu {now} pendant l'essai : à rejuger contre lui ; {score}")
            : null;

    /// <summary>Résultat du banc : situations comparées, gagnées par chacun, et écart situation par situation.</summary>
    private sealed record BenchOutcome(int Cases, int CandidateWins, int ChampionWins, int Plus, int Minus);

    /// <summary>Fait jouer le banc au champion (résultats gardés au journal) puis au candidat, et les compare.</summary>
    private async Task<(BenchOutcome Outcome, string? CandidateFault)> RunBenchAsync(
        string hash, string candidateExe, string championHash, string championExe, IReadOnlyDictionary<string, string> exes, CancellationToken ct)
    {
        var champion = await ctx.PlayBenchAsync(championHash, championExe, exes, null, log, ct);
        var cases = champion.Keys.ToList();
        Telemetry.SetBenchWinRate((double)cases.Count(c => champion[c] == Results.Win) / cases.Count);

        string? fault = null;
        var candidate = await ctx.PlayBenchAsync(hash, candidateExe, exes, (c, r) => fault ??= $"contre {c.Opponent} : {r.Detail}", log, ct);
        if (fault is not null)
        {
            return (new BenchOutcome(cases.Count, 0, 0, 0, 0), fault);
        }

        var (plus, minus, cw, chw) = Bench.Compare(cases, candidate, champion);
        return (new BenchOutcome(cases.Count, cw, chw, plus, minus), null);
    }

    /// <summary>Longest wait for the test game quota before giving up on a confirmation (the 6-hour window).</summary>
    private static readonly TimeSpan MaxQuotaWait = TimeSpan.FromHours(6);

    /// <summary>
    /// Waits until CodinGame's large windows can hold the whole confirmation: a candidate that won
    /// its duel is worth waiting for. Beyond <see cref="MaxQuotaWait"/>, the quota pause takes over.
    /// </summary>
    private async Task WaitForTestGamesAsync(int needed, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (await api.TestGameShortfallAsync(ctx.Config.Jeu, needed, ct) is { } shortfall)
        {
            if (waited >= MaxQuotaWait)
            {
                throw new QuotaExhaustedException($"Confirmation impossible : {shortfall}", TimeSpan.FromMinutes(30));
            }

            log.LogInformation("Confirmation : {Manque}, nouvelle vérification dans 5 min", shortfall);
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            waited += TimeSpan.FromMinutes(5);
        }
    }

    /// <summary>
    /// Adversaires de la confirmation : des joueurs classés au-dessus de nous, choisis par une règle
    /// fixe (<see cref="Confirmation.PickOpponents"/>), ou <c>api.adversaire</c> si aucun n'est demandé
    /// ou si le classement est indisponible.
    /// </summary>
    private async Task<IReadOnlyList<string>> ConfirmationOpponentsAsync(CancellationToken ct)
    {
        var cfg = ctx.Config.Api;
        if (cfg.ConfirmationJoueurs <= 0)
        {
            return [cfg.Adversaire];
        }

        var me = await api.GetRankingAsync(ctx.Config.Jeu, ct);
        var players = await api.GetOpponentsAsync(ctx.Config.Jeu, ct);
        var picked = me is null ? [] : Confirmation.PickOpponents(players, me.LeagueRank, cfg.ConfirmationJoueurs, cfg.ConfirmationRangsAuDessus);
        if (picked.Count == 0)
        {
            log.LogWarning("Confirmation : aucun joueur à affronter (classement indisponible), repli sur {Adversaire}", cfg.Adversaire);
            return [cfg.Adversaire];
        }

        log.LogInformation("Confirmation contre {N} joueurs classés au-dessus du rang {Rang} : {Agents}", picked.Count, me!.LeagueRank, string.Join(", ", picked));
        return picked;
    }

    /// <summary>Joue des parties de test sur CodinGame : une par option donnée (null = circuit tiré au hasard).</summary>
    private async Task<List<ApiGameEntry>> PlayApiGamesAsync(
        string hash, string source, string opponent, IReadOnlyList<string?> options, long? trialId, CancellationToken ct)
    {
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
            ctx.Journal.AddAnalysis(await RaceAnalysis.OfGameAsync("test", result.Log, hash, opponent, trialId, ctx.Config.Analyse, ctx.Referee, ctx.Config.Arene.Limits(), ct));
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
        var lookPrompt = await WriteWorkspaceAsync(lookDir, "regard_neuf", ct,
            ("RULES.md", ReadOrEmpty(ctx.Paths.Rules)),
            ("ENGINE.md", ReadOrEmpty(ctx.Paths.EngineNotes)),
            ("STRATEGY.md", ReadOrEmpty(ctx.Paths.Strategy)),
            (cfg.Bot.Fichier, championSource),
            ("HISTORY.md", History(int.MaxValue)),
            ("LOSSES.md", Losses(ctx.Journal.ChampionHash() ?? "")),
            ("GUIDANCE.md", ReadOrEmpty(ctx.Paths.UserGuidance())),
            ("PREVIOUS_ORIENTATION.md", ReadOrEmpty(ctx.Paths.Orientation)));
        var look = await AskAsync(id, new AgentRequest("regard_neuf", cfg.Agents.RegardNeuf, lookDir, lookPrompt, TimeSpan.FromMinutes(cfg.Agents.BlocageMinutes)), ct);
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
        ctx.Journal.AddTrialTokens(id, request.Role, result.Tokens);
        for (var retry = 0; retry < ctx.Config.Boucle.RelancesReponseVide
             && result.Success && string.IsNullOrWhiteSpace(result.Response); retry++)
        {
            log.LogWarning("Essai {Essai} : réponse vide du {Role}, relance {N} — sortie de l'outil : {Diagnostic}",
                id, request.Role, retry + 1, result.Diagnostic);
            result = await agents.RunAsync(request, ct);
            ctx.Journal.AddTrialTokens(id, request.Role, result.Tokens);
        }

        if (result.Success && string.IsNullOrWhiteSpace(result.Response))
        {
            log.LogWarning("Essai {Essai} : réponse encore vide du {Role} — sortie de l'outil : {Diagnostic}", id, request.Role, result.Diagnostic);
        }

        return result;
    }

    /// <summary>
    /// Prépare le dossier de l'agent et renvoie sa consigne. Les documents sont recopiés dans la
    /// consigne elle-même : chaque lecture de fichier par l'agent renverrait tout le contexte au
    /// modèle, et une lecture hors du dossier ferait échouer le run. Les fichiers restent écrits
    /// dans le dossier, pour l'audit et pour les consignes trop longues.
    /// </summary>
    private async Task<string> WriteWorkspaceAsync(string dir, string role, CancellationToken ct, params (string Name, string Content)[] files)
    {
        Directory.CreateDirectory(dir);
        var task = ctx.Fill(await File.ReadAllTextAsync(ctx.Paths.Prompt(role), ct));
        await File.WriteAllTextAsync(Path.Combine(dir, "TASK.md"), task, ct);
        foreach (var (name, content) in files)
        {
            if (!string.IsNullOrWhiteSpace(content))
            {
                await File.WriteAllTextAsync(Path.Combine(dir, name), content, ct);
            }
        }

        await agents.GrantAsync(dir, ct);

        var prompt = new StringBuilder(task)
            .Append("\n\n---\n\n# Documents\n\nAll the useful documents are below. Do not read any file: ")
            .Append("the only file to touch, if any, is the one your task asks you to write.\n\n");
        foreach (var (name, content) in files.Where(f => !string.IsNullOrWhiteSpace(f.Content)))
        {
            prompt.Append("## ").Append(name).Append("\n\n````\n").Append(content.TrimEnd()).Append("\n````\n\n");
        }

        var inline = prompt.ToString();
        if (Encoding.UTF8.GetByteCount(inline) <= MaxInlinePromptBytes)
        {
            return inline;
        }

        log.LogWarning("Consigne du {Role} trop longue pour être passée directement : l'agent lira ses fichiers", role);
        return Instruction;
    }

    /// <summary>Résumé du journal donné aux agents : les derniers essais, puis toutes les idées déjà jetées.</summary>
    private string History(int recent)
    {
        var all = ctx.Journal.AllTrials();
        var sb = new StringBuilder("# Trial history\n\n");
        var champions = ctx.Journal.ChampionCount();
        sb.Append(CultureInfo.InvariantCulture, $"Promotions since the starting bot: {champions - 1}. Finished trials: {all.Count}.\n\n");
        if (all.Count == 0)
        {
            return sb.Append("No trial yet: this is the first one.\n").ToString();
        }

        sb.Append("## Latest trials (most recent first)\n\n")
          .Append("Target: the cause of LOSSES.md the idea aimed at. Measured: candidate vs champion, average per local duel game ")
          .Append("(runner pace in turns per checkpoint on collision-free stretches, lower is faster; hits suffered by the runner; ")
          .Append("collisions between the bot's own pods).\n\n")
          .Append("| # | Verdict | Target | Idea | Against the champion | Measured | Reason |\n|---|---|---|---|---|---|---|\n");
        foreach (var t in all.TakeLast(Math.Min(recent, all.Count)).Reverse())
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"| {t.Id} | {t.Verdict} | {t.Target ?? "-"} | {Cell(t.Idea)} | {t.Wins} W / {t.Losses} L | {Cell(t.Measures ?? "-")} | {Cell(t.Reason)} |\n");
        }

        // The real leaderboard, the only goal: what each recent champion actually scored.
        var scored = ctx.Journal.ScoredChampions().TakeLast(8).ToList();
        if (scored.Count > 0)
        {
            sb.Append("\n## Real CodinGame score of the latest champions (oldest first)\n\n");
            foreach (var c in scored)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"- {(c.TrialId is { } t ? $"trial #{t}" : "starting bot")}: score {c.Score:0.00}, rank {c.Rank}{(c.Retired ? " (withdrawn: scored clearly lower than the previous champion)" : "")}\n");
            }
        }

        // A refused note was never coded: its idea was not tried.
        var rejected = all.Where(t => t.Verdict is not Verdicts.Kept and not Verdicts.BadNote and not null && t.Idea is not null)
            .TakeLast(ctx.Config.Contexte.IdeesRejetees).ToList();
        if (rejected.Count > 0)
        {
            sb.Append("\n## Ideas already tried without success\n\n");
            foreach (var t in rejected)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- #{t.Id} ({t.Verdict}) : {Cell(t.Idea)}\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>LOSSES.md: why the champion loses its real games (ranked and confirmation games, replayed).</summary>
    private string Losses(string championHash) =>
        LossReport.ForAgents(ctx.Journal.Analyses(championHash, "battle", "test"), ctx.Config.Analyse);

    /// <summary>
    /// The thinker's answer without its draft: a model sometimes sends its reasoning before the
    /// answer, with earlier versions and the format of the prompt quoted. The answer starts at the
    /// last « IDEA: » label (with its markdown); a response without the label is kept whole.
    /// </summary>
    public static string AnswerOf(string response)
    {
        var start = response.LastIndexOf("IDEA:", StringComparison.Ordinal);
        if (start < 0)
        {
            return response;
        }

        while (start > 0 && response[start - 1] is '*' or '#' or ' ' or '\t')
        {
            start--;
        }

        return response[start..].Trim();
    }

    /// <summary>The cause a note aims at: the first word after « TARGET: » (or on the next line), if it names a known cause.</summary>
    public static string? TargetOf(string note)
    {
        var lines = note.Split('\n').Select(l => l.Trim().Trim('*', '`').Trim().TrimStart('#', '-', ' ')).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("TARGET", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = lines[i][6..].TrimStart(':', ' ', '*', '`', '\t');
            if (value.Length == 0)
            {
                value = lines.Skip(i + 1).FirstOrDefault(x => x.Length > 0) ?? "";
            }

            var word = new string(value.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
            return LossCauses.All.Contains(word) ? word : null;
        }

        return null;
    }

    /// <summary>
    /// L'idée d'une note : le texte après « IDEE : », sur la même ligne ou, si le modèle est passé
    /// à la ligne, sur la première ligne non vide qui suit.
    /// </summary>
    public static string IdeaOf(string note) =>
        Clip(LabeledIdea(note) ?? note.Split('\n').Select(l => l.Trim().Trim('*').Trim()).FirstOrDefault(l => l.Length > 0) ?? "", 200);

    /// <summary>The text after the idea label, or null when the note has no idea label.</summary>
    private static string? LabeledIdea(string note)
    {
        var lines = note.Split('\n').Select(l => l.Trim().Trim('*').Trim()).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i].TrimStart('#', '-', ' ');
            // Notes in English (IDEA:) since 2026-11-16; older notes in French (IDEE / IDÉE).
            if (l.StartsWith("IDEA", StringComparison.OrdinalIgnoreCase) || l.StartsWith("IDEE", StringComparison.OrdinalIgnoreCase)
                || l.StartsWith("IDÉE", StringComparison.OrdinalIgnoreCase))
            {
                var value = l[4..].TrimStart(':', ' ', '*', '\t');
                if (value.Length == 0)
                {
                    value = lines.Skip(i + 1).FirstOrDefault(x => x.Length > 0) ?? "";
                }

                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What makes a thinker's note unusable, written for the thinker; empty when the note is fine.
    /// <paramref name="task"/> is the thinker's prompt: an idea found in it was copied from the format.
    /// </summary>
    public static List<string> NoteProblems(string note, string task, int maxWords)
    {
        var problems = new List<string>();
        var idea = LabeledIdea(note);
        if (idea is null)
        {
            problems.Add("there is no IDEA: line");
        }
        else if (task.Contains(idea, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"the IDEA line (\"{Clip(idea, 60)}\") is copied from the format of the task instead of stating your idea");
        }

        if (TargetOf(note) is null)
        {
            problems.Add($"there is no TARGET: line naming one cause: {string.Join(", ", LossCauses.All)}");
        }

        var words = note.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > maxWords)
        {
            problems.Add($"the note has {words} words, the limit is {maxWords}: write only the final note, without your reasoning");
        }

        return problems;
    }

    private static string Refusal(IEnumerable<string> problems) =>
        "\n\n---\n\n# A previous answer to this task was refused\n\n"
        + string.Concat(problems.Select(p => $"- {p.TrimEnd('.')}.\n"))
        + "\nWrite the note again, in the format of the task and nothing else.\n";

    private static string Label(Config.AgentRole r) => $"{r.Outil}/{r.Modele}";

    private static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();

    private static string Cell(string? s) => Clip((s ?? "").Replace('\n', ' ').Replace("|", "/"), 160);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
