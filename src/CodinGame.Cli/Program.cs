using System.Globalization;
using CodinGame.Core;
using CodinGame.Core.Agents;
using CodinGame.Core.Arena;
using CodinGame.Core.CodinGameApi;
using CodinGame.Core.Config;
using CodinGame.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// farm <commande> [arguments]
//   servir                 boucle continue (par défaut, c'est ce que lance le conteneur)
//   essai [pipeline]       un seul essai
//   verifier [pipeline]    contrôle des prérequis, sans rien dépenser d'autre qu'une lecture d'API
//   bilan [pipeline]       résumé du journal
//   duel <a> <b> [n]       duel local entre deux sources de bot, avec le moteur de la pipeline

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
builder.Services.AddSingleton<IAgentRunner, AntigravityRunner>();
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

    await farm.RunForeverAsync(ct);
    return 0;
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

        if (await host.Services.GetRequiredService<CodinGameClient>().TestGamesLeftAsync(ctx.Config.Jeu, ct) is { } left)
        {
            Console.WriteLine($"         parties de test restantes : {left}");
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
        var settings = rest.Length > 2
            ? ctx.Config.Arene with { PartiesMax = int.Parse(rest[2], CultureInfo.InvariantCulture), PartiesMin = Math.Min(ctx.Config.Arene.PartiesMin, int.Parse(rest[2], CultureInfo.InvariantCulture)) }
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

static string Clip(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max] + "…";

static int Usage()
{
    Console.Error.WriteLine("Usage : farm [servir | essai | verifier | bilan | duel <a> <b> [n]]");
    return 64;
}
