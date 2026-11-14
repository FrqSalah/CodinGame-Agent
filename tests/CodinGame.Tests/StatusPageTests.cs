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
        Now);

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
    public void Renders_before_the_first_ranking_is_recorded()
    {
        var html = StatusPage.Render(Data([]));

        Assert.Contains("Aucun relevé encore", html);
        Assert.DoesNotContain("<svg", html);
    }
}
