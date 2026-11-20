using CodinGame.Core.Data;
using CodinGame.Core.Report;

namespace CodinGame.Tests;

public class StatusPageTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 22, 0, 0, DateTimeKind.Utc);

    private static StatusData Data(IReadOnlyList<RankingEntry> rankings) => new(
        "mad-pod-racing",
        rankings,
        [new ChampionEntry(1, "abcdef0123456789", 113, Now.AddHours(-8))],
        [
            new TrialSummary(120, Now.AddHours(-2).ToString("O"), "JETE", "pas d'amélioration", "Idée <script>alert(1)</script>", 55, 74, 0, "p", "c"),
            new TrialSummary(121, Now.AddHours(-1).ToString("O"), "ECHEC_AGENT", "penseur : réponse coupée", null, 0, 0, 0, "p", "c"),
        ],
        (122, Now.AddMinutes(-5), null),
        [("bot353", 39, 40)],
        new Dictionary<string, DateTime>(),
        (10, 150_000, 25_000, 4_000, 179_000),
        "",
        Now,
        TimeSpan.FromMinutes(60));

    [Fact]
    public void Shows_the_ranking_progress_and_escapes_agent_text()
    {
        var html = StatusPage.Render(Data(
        [
            new RankingEntry(Now.AddDays(-1), 1066, 236_690, 24.31, "Legend", 1066, 2225, 100),
            new RankingEntry(Now, 169, 236_736, 38.4, "Legend", 169, 2263, 100),
        ]));

        Assert.Contains("169", html);
        Assert.Contains("▲ 897 places", html);
        Assert.Contains("<svg", html);
        Assert.Contains("penseur : réponse coupée", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Essai <b>#122</b> en cours", html);
    }

    [Fact]
    public void Warns_when_the_ranking_was_not_recorded_for_a_long_time()
    {
        var html = StatusPage.Render(Data([new RankingEntry(Now.AddHours(-34), 124, 236_701, 39.42, "Legend", 124, 2263, 100)]));

        Assert.Contains("Classement non relevé depuis 34 h 00", html);
        Assert.DoesNotContain("Dernier relevé il y a", html);
    }

    [Fact]
    public void Shows_the_best_rank_held_ignoring_peaks_while_ranking()
    {
        var html = StatusPage.Render(Data(
        [
            new RankingEntry(Now.AddDays(-2), 102, 236_700, 40.67, "Legend", 102, 2297, 100),
            new RankingEntry(Now.AddDays(-1), 90, 236_700, 41.14, "Legend", 90, 2297, 41),
            new RankingEntry(Now, 105, 236_714, 40.6, "Legend", 105, 2297, 100),
        ]));

        Assert.Contains("Meilleur rang</div><div class=\"value\">102</div>", html);
        Assert.Contains("actuel à 3 places", html);
    }

    [Fact]
    public void Says_when_the_current_rank_is_the_record()
    {
        var html = StatusPage.Render(Data(
        [
            new RankingEntry(Now.AddDays(-1), 120, 236_700, 40.0, "Legend", 120, 2297, 100),
            new RankingEntry(Now, 103, 236_714, 40.67, "Legend", 103, 2297, 100),
        ]));

        Assert.Contains("Meilleur rang</div><div class=\"value\">103</div>", html);
        Assert.Contains("record actuel", html);
    }

    [Fact]
    public void Shows_why_the_champion_loses()
    {
        var data = Data([new RankingEntry(Now, 103, 236_714, 40.67, "Legend", 103, 2297, 100)]);

        var html = StatusPage.Render(data with { Losses = new(40, 28, 12, [("blocking", 9), ("speed", 3)]) });
        var none = StatusPage.Render(data with { Losses = new(0, 0, 0, []), RealGames24h = 0 });
        var recent = StatusPage.Render(data with { Losses = new(40, 28, 12, []), RealGames24h = 31 });

        Assert.Contains("Pourquoi on perd", html);
        Assert.Contains("Bataille de blocage perdue</td><td class='num'>9 · 75 %", html);
        Assert.Contains("la ferme rejoue ses parties classées", none);
        Assert.Contains("<span class='down'>Aucune vraie partie mesurée depuis 24 h.</span>", none);
        Assert.Contains("31 vraies parties rejouées et mesurées ces dernières 24 h", recent);
    }

    [Fact]
    public void Renders_before_the_first_ranking_is_recorded()
    {
        var html = StatusPage.Render(Data([]));

        Assert.Contains("Aucun relevé encore", html);
        Assert.DoesNotContain("<svg", html);
    }

    [Fact]
    public void Warns_about_refused_thinker_notes_only_when_there_are_some()
    {
        var clean = StatusPage.Render(Data([]));
        var data = Data([]);
        var refused = StatusPage.Render(data with
        {
            Trials = [.. data.Trials, new TrialSummary(122, Now.AddMinutes(-30).ToString("O"), "NOTE_REFUSEE", "note du penseur refusée deux fois : there is no IDEA: line", null, 0, 0, 0, "p", "c")],
        });

        Assert.DoesNotContain("note(s) du penseur refusée(s)", clean);
        Assert.Contains("<span class='down'>1 note(s) du penseur refusée(s)</span>", refused);
        Assert.Contains("<span class=\"badge failed\">NOTE_REFUSEE</span>", refused);
    }
}
