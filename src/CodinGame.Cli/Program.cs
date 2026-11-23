using System.Globalization;
using CodinGame.Core;
using CodinGame.Core.Agents;
using CodinGame.Core.Arena;
using CodinGame.Core.CodinGameApi;
using CodinGame.Core.Config;
using CodinGame.Core.Data;
using CodinGame.Core.Pipeline;
using CodinGame.Core.Report;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// farm <commande> [arguments]
//   servir                 boucle continue (par défaut, c'est ce que lance le conteneur)
//   essai [pipeline]       un seul essai
//   verifier [pipeline]    contrôle des prérequis, sans rien dépenser d'autre qu'une lecture d'API
//   bilan [pipeline]       résumé du journal
//   duel <a> <b> [n]       duel local entre deux sources de bot, avec le moteur de la pipeline
//   rejuger <essai>        rejuge le candidat d'un essai passé (duel, confirmation, promotion)
//   juger <source> <idée> [note]  juge un bot écrit hors de la ferme, avec les mêmes étapes et seuils
//   banc                   mesure le champion contre les adversaires extérieurs (résultats gardés au journal)
//   page <fichier.html>    écrit la page de suivi dans un fichier (servie en direct par « servir » si FARM_WEB_URL)

var command = args.FirstOrDefault() ?? "servir";
var rest = args.Skip(1).ToArray();

var builder = Host.CreateApplicationBuilder([]);
builder.AddServiceDefaults();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
builder.Services.AddSingleton(FarmPaths.FromEnvironment());
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(100) });
builder.Services.AddSingleton(sp => new CodinGameClient(
    sp.GetRequiredService<HttpClient>(),
    sp.GetRequiredService<FarmPaths>().TokenFile,
    sp.GetRequiredService<ILogger<CodinGameClient>>()));
builder.Services.AddSingleton<AntigravityRunner>();
builder.Services.AddSingleton<IAgentRunner>(sp => new FallbackAgentRunner(
    sp.GetRequiredService<AntigravityRunner>(),
    sp.GetRequiredService<FarmPaths>(),
    sp.GetRequiredService<ILogger<FallbackAgentRunner>>()));
builder.Services.AddSingleton<Farm>();

using var host = builder.Build();
await host.StartAsync();
var ct = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
var paths = host.Services.GetRequiredService<FarmPaths>();
var farm = host.Services.GetRequiredService<Farm>();
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("farm");

int code;
try
{
    code = command switch
    {
        "servir" => await Serve(),
        "essai" => await RunTrial(),
        "verifier" => await Check(),
        "bilan" => Report(),
        "duel" => await LocalDuel(),
        "banc" => await BenchChampion(),
        "page" when rest.Length > 0 => WritePage(rest[0]),
        "rejuger" when rest.Length > 0 => (await farm.RejudgeAsync(paths.PipelineNames().First(), long.Parse(rest[0], CultureInfo.InvariantCulture), ct)).Verdict == Verdicts.Kept ? 0 : 2,
        "juger" when rest.Length > 1 => (await farm.JudgeGivenAsync(paths.PipelineNames().First(),
            new GivenCandidate(await File.ReadAllTextAsync(rest[0], ct), rest[1], rest.Length > 2 ? await File.ReadAllTextAsync(rest[2], ct) : rest[1]), ct)).Verdict == Verdicts.Kept ? 0 : 2,
        _ => Usage(),
    };
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    code = 0;
}
catch (Exception ex) when (ex is ConfigException or CodinGameException)
{
    log.LogError("{Message}", ex.Message);
    code = 1;
}

await host.StopAsync();
return code;

string PipelineArg(int index = 0) =>
    rest.Length > index ? rest[index] : paths.PipelineNames().FirstOrDefault()
        ?? throw new ConfigException($"aucune pipeline trouvée dans {paths.SeedRoot}");

async Task<int> Serve()
{
    if (Environment.GetEnvironmentVariable("FARM_AGENT_USER") is { } user)
    {
        AntigravityRunner.EnsurePermissions($"/home/{user}", [Path.Combine(paths.DataRoot, "pipelines"), paths.SeedRoot]);
        await host.Services.GetRequiredService<IAgentRunner>().GrantAsync($"/home/{user}/.gemini", ct);
    }

    // Page de suivi : seulement si une adresse d'écoute est donnée (FARM_WEB_URL, dans le compose).
    WebApplication? web = null;
    if (Environment.GetEnvironmentVariable("FARM_WEB_URL") is { Length: > 0 } url)
    {
        var wb = WebApplication.CreateSlimBuilder();
        wb.WebHost.UseUrls(url);
        wb.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // pas cinq lignes par visite
        web = wb.Build();
        web.MapGet("/", () => TypedResults.Content(RenderStatus(), "text/html; charset=utf-8"));
        await web.StartAsync(ct);
        log.LogInformation("Page de suivi servie sur {Url}", url);
    }

    var rankings = farm.RecordRankingsForeverAsync(ct);
    try
    {
        await farm.RunForeverAsync(ct);
    }
    finally
    {
        if (web is not null)
        {
            await web.StopAsync(CancellationToken.None);
        }
    }

    await rankings.ContinueWith(_ => { }, TaskScheduler.Default);
    return 0;
}

int WritePage(string file)
{
    File.WriteAllText(file, RenderStatus());
    Console.WriteLine($"Page de suivi écrite dans {file}");
    return 0;
}

string RenderStatus()
{
    var name = paths.PipelineNames().First();
    var p = paths.Pipeline(name);
    using var journal = new Journal(p.Journal);
    var champion = journal.ChampionHash();
    var blocked = host.Services.GetRequiredService<IAgentRunner>() is FallbackAgentRunner chain
        ? chain.Blocked()
        : new Dictionary<string, DateTime>();
    return StatusPage.Render(new StatusData(
        name,
        journal.Rankings(),
        journal.Champions(),
        journal.AllTrials(),
        journal.CurrentTrial(),
        champion is null ? [] : journal.BenchSummary(champion),
        blocked,
        journal.TokenAverages(10),
        File.Exists(p.Orientation) ? File.ReadAllText(p.Orientation) : "",
        DateTime.UtcNow,
        TimeSpan.FromMinutes(ConfigLoader.Load(p.ConfigSeed, p.ConfigData).Api.ReleveClassementMinutes),
        journal.ScoredChampions(),
        CodinGame.Core.Analysis.LossReport.Breakdown(champion is null ? [] : journal.Analyses(champion, "battle", "test")),
        journal.RealGamesMeasuredSince(DateTime.UtcNow.AddHours(-24))));
}

async Task<int> RunTrial() => await farm.RunOnceAsync(PipelineArg(), ct) ? 0 : 2;

async Task<int> Check()
{
    var name = PipelineArg();
    var ok = true;
    void Line(bool pass, string what)
    {
        ok &= pass;
        Console.WriteLine($"{(pass ? "[ok]   " : "[ÉCHEC]")} {what}");
    }

    Line(File.Exists(paths.TokenFile), "token CodinGame présent (son contenu n'est jamais affiché)");
    var (ctx, journal) = farm.Open(name);
    using (journal)
    {
        Line(true, $"configuration valide : {ctx.Paths.ConfigData}");
        try
        {
            var ready = await farm.PrepareAsync(ctx, ct);
            Line(ready, $"API CodinGame, ligue couverte ({string.Join(", ", ctx.Config.Ligues)}) et moteur local");
            Line(journal.ChampionHash() is not null, $"champion compilé ({journal.ChampionHash()})");
        }
        catch (Exception ex) when (ex is CodinGameException or ConfigException)
        {
            Line(false, ex.Message);
        }

        var status = await host.Services.GetRequiredService<IAgentRunner>().CheckAsync(ct);
        Line(status.Availability == AgentAvailability.Ready, $"Antigravity : {status.Detail}");
        if (status.Models.Count > 0)
        {
            Console.WriteLine($"         modèles : {string.Join(", ", status.Models)}");
        }

        if (host.Services.GetRequiredService<IAgentRunner>() is FallbackAgentRunner chain && chain.Blocked().Count > 0)
        {
            Console.WriteLine($"         modèles écartés (limite atteinte) : {string.Join(", ", chain.Blocked().Select(b => $"{b.Key} jusqu'à {b.Value:HH:mm} UTC"))}");
        }

        var windows = await host.Services.GetRequiredService<CodinGameClient>().TestGameWindowsAsync(ctx.Config.Jeu, ct);
        if (windows.Count > 0)
        {
            // Every window, not just the tightest: 10 left out of 10 per minute is not a shortage.
            Console.WriteLine($"         parties de test restantes : {string.Join(", ", windows.Select(w => $"{w.Name.Replace("test_games_", "")} {w.Remaining}/{w.Limit}"))}");
            Console.WriteLine($"         une confirmation en demande {ctx.Config.Api.ConfirmationCost}");
        }
    }

    return ok ? 0 : 1;
}

int Report()
{
    var name = PipelineArg();
    var (ctx, journal) = farm.Open(name);
    using (journal)
    {
        var trials = journal.AllTrials();
        Console.WriteLine($"Pipeline {name} — champion {journal.ChampionHash() ?? "(aucun)"}, {journal.ChampionCount() - 1} promotion(s)");
        foreach (var group in trials.GroupBy(t => t.Verdict ?? "?").OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Key,-12} {group.Count()}");
        }

        var tokens = journal.TokenAverages(10);
        if (tokens.Trials > 0)
        {
            Console.WriteLine($"  tokens moyens sur les {tokens.Trials} derniers essais mesurés : penseur {tokens.Penseur:N0}, codeur {tokens.Codeur:N0}, regard neuf {tokens.RegardNeuf:N0}, total {tokens.Total:N0}");
        }

        Console.WriteLine();
        foreach (var t in trials.TakeLast(15).Reverse())
        {
            Console.WriteLine($"#{t.Id,-4} {t.Verdict,-12} {t.Wins,4} V {t.Losses,4} D  {Clip(t.Idea, 70)}");
        }
    }

    return 0;
}

async Task<int> LocalDuel()
{
    if (rest.Length < 2)
    {
        return Usage();
    }

    var name = paths.PipelineNames().First();
    var (ctx, journal) = farm.Open(name);
    using (journal)
    {
        // A duel of n games stops at n: no second round.
        var n = rest.Length > 2 ? int.Parse(rest[2], CultureInfo.InvariantCulture) : 0;
        var settings = n > 0
            ? ctx.Config.Arene with { PartiesMax = n, PartiesMin = Math.Min(ctx.Config.Arene.PartiesMin, n), PartiesProlongation = n }
            : ctx.Config.Arene;
        var a = await ctx.Builder.BuildAsync(await File.ReadAllTextAsync(rest[0], ct), ct);
        var b = await ctx.Builder.BuildAsync(await File.ReadAllTextAsync(rest[1], ct), ct);
        if (!a.Success || !b.Success)
        {
            log.LogError("Compilation impossible : {Erreurs}", a.Success ? b.Errors : a.Errors);
            return 1;
        }

        var outcome = await new Duel(ctx.Referee, settings, log).RunAsync(a.Executable!, b.Executable!, null, ct);
        Console.WriteLine($"{Path.GetFileName(rest[0])} contre {Path.GetFileName(rest[1])} : {outcome.Wins} V / {outcome.Losses} D / {outcome.Draws} N, LLR {outcome.Llr:F2}, décision {outcome.Decision}{(outcome.CandidateFault is null ? "" : $", faute : {outcome.CandidateFault}")}");
    }

    return 0;
}

async Task<int> BenchChampion()
{
    var (ctx, journal) = farm.Open(paths.PipelineNames().First());
    using (journal)
    {
        if (ctx.Config.Banc.Adversaires.Count == 0)
        {
            log.LogError("Aucun adversaire dans banc.adversaires");
            return 1;
        }

        var hash = journal.ChampionHash() ?? throw new ConfigException("aucun champion");
        var build = await ctx.Builder.BuildAsync(journal.BotSource(hash)!, ct);
        var opponents = await ctx.BuildBenchOpponentsAsync(ct);
        var results = await ctx.PlayBenchAsync(hash, build.Executable!, opponents, null, log, ct);
        Console.WriteLine($"Champion {hash} contre le banc :");
        foreach (var group in results.GroupBy(r => r.Key.Opponent))
        {
            var wins = group.Count(r => r.Value == CodinGame.Core.Arena.Results.Win);
            Console.WriteLine($"  {group.Key,-34} {wins,3}/{group.Count()} victoires ({100.0 * wins / group.Count():F0} %)");
        }
    }

    return 0;
}

static string Clip(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max] + "…";

static int Usage()
{
    Console.Error.WriteLine("Usage : farm [servir | essai | verifier | bilan | duel <a> <b> [n] | rejuger <essai> | juger <source> <idée> [note] | banc | page <fichier.html>]");
    return 64;
}
