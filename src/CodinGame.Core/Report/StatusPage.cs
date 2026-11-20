using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text;
using CodinGame.Core.Data;

namespace CodinGame.Core.Report;

/// <summary>Ce que la page de suivi affiche d'une pipeline.</summary>
public sealed record StatusData(
    string Pipeline,
    IReadOnlyList<RankingEntry> Rankings,
    IReadOnlyList<ChampionEntry> Champions,
    IReadOnlyList<TrialSummary> Trials,
    (long Id, DateTime Start, string? Idea)? Current,
    IReadOnlyList<(string Opponent, int Wins, int Games)> Bench,
    IReadOnlyDictionary<string, DateTime> BlockedModels,
    (int Trials, double Penseur, double Codeur, double RegardNeuf, double Total) Tokens,
    string Orientation,
    DateTime Now,
    TimeSpan RankingInterval,
    IReadOnlyList<ScoredChampion>? Scored = null,
    Analysis.LossBreakdown? Losses = null,
    int? RealGames24h = null);

/// <summary>
/// Page de suivi de la ferme, en HTML simple : classement dans le temps, champion, essais,
/// agents. Lecture seule ; aucune donnée secrète n'y figure.
/// </summary>
public static class StatusPage
{
    /// <summary>Formats français, construits à la main : le programme tourne sans données de cultures.</summary>
    private static readonly CultureInfo Fr = French();

    private static CultureInfo French()
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        c.NumberFormat.NumberDecimalSeparator = ",";
        c.NumberFormat.NumberGroupSeparator = " ";
        string[] months = ["janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre", ""];
        string[] abbreviated = ["janv.", "févr.", "mars", "avr.", "mai", "juin", "juil.", "août", "sept.", "oct.", "nov.", "déc.", ""];
        c.DateTimeFormat.MonthNames = months;
        c.DateTimeFormat.MonthGenitiveNames = months;
        c.DateTimeFormat.AbbreviatedMonthNames = abbreviated;
        c.DateTimeFormat.AbbreviatedMonthGenitiveNames = abbreviated;
        return c;
    }

    public static string Render(StatusData d)
    {
        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <html lang="fr">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="refresh" content="60">
            <title>Ferme {{E(d.Pipeline)}}</title>
            <style>{{Css}}</style>
            </head>
            <body>
            <header>
              <h1>Ferme <span>{{E(d.Pipeline)}}</span></h1>
              <p class="muted">Mis à jour le {{d.Now.ToString("d MMMM à HH:mm", Fr)}} UTC · rafraîchi chaque minute</p>
            </header>
            <main>
            """);

        RankingSection(html, d);
        FarmSection(html, d);
        LossesSection(html, d);
        TrialsSection(html, d);
        html.Append("</main></body></html>");
        return html.ToString();
    }

    private static void RankingSection(StringBuilder html, StatusData d)
    {
        html.Append("<section><h2>Classement</h2>");
        if (d.Rankings.Count == 0)
        {
            html.Append("<p class=\"muted\">Aucun relevé encore : le premier arrive dans l'heure qui suit le démarrage.</p></section>");
            return;
        }

        var last = d.Rankings[^1];
        var first = d.Rankings[0];
        html.Append("<div class=\"tiles\">");
        Tile(html, "Rang mondial", $"{last.Rank.ToString("N0", Fr)}<small> / {last.Total.ToString("N0", Fr)}</small>",
            Delta(first.Rank - last.Rank, $"depuis le {first.At.ToString("d MMM", Fr)}", "places"));
        Tile(html, "Score", last.Score.ToString("0.00", Fr), Delta(last.Score - first.Score, $"depuis le {first.At.ToString("d MMM", Fr)}", "points"));
        Tile(html, $"Ligue {E(last.League)}", $"{last.LeagueRank.ToString("N0", Fr)}<small> / {last.LeagueTotal.ToString("N0", Fr)}</small>",
            last.Percentage < 100 ? $"<span class=\"muted\">classement en cours : {last.Percentage} %</span>" : "<span class=\"muted\">classement stable</span>");
        RecordTile(html, d.Rankings, last);
        html.Append("</div>");

        // Un relevé en retard se voit : sinon la page affiche un classement périmé sans le dire.
        var age = d.Now - last.At;
        html.Append(age > 2 * d.RankingInterval + TimeSpan.FromMinutes(5)
            ? $"<p class=\"alert\">Classement non relevé depuis {Minutes(age)} (un relevé est prévu toutes les {(int)d.RankingInterval.TotalMinutes} min) : les chiffres ci-dessus sont périmés.</p>"
            : $"<p class=\"muted\">Dernier relevé il y a {Minutes(age)}.</p>");
        if (d.Rankings.Count >= 2)
        {
            html.Append(ScoreChart(d.Rankings, d.Champions));
        }

        html.Append("</section>");
    }

    private static void FarmSection(StringBuilder html, StatusData d)
    {
        html.Append("<section class=\"grid\">");

        // Champion
        html.Append("<div class=\"card\"><h2>Champion</h2>");
        if (d.Champions.Count > 0)
        {
            var c = d.Champions[^1];
            html.Append($"<p><code>{E(c.Hash[..Math.Min(12, c.Hash.Length)])}</code> · essai {(c.TrialId is { } t ? "#" + t : "de départ")} · depuis le {c.Since.ToString("d MMM à HH:mm", Fr)}</p>");
            html.Append($"<p class=\"muted\">{d.Champions.Count - 1} promotion(s) depuis le début</p>");
        }

        if (d.Scored is { Count: > 0 } scored)
        {
            html.Append("<table><thead><tr><th>Score réel des derniers champions</th><th class=\"num\">Score</th><th class=\"num\">Rang</th></tr></thead><tbody>");
            foreach (var c in scored.TakeLast(6).Reverse())
            {
                var label = c.TrialId is { } t ? $"essai #{t}" : "bot de départ";
                html.Append($"<tr><td>{label}{(c.Retired ? " <span class=\"badge failed\">retiré</span>" : "")}</td>");
                html.Append($"<td class=\"num\">{c.Score.ToString("0.00", Fr)}</td><td class=\"num\">{c.Rank}</td></tr>");
            }

            html.Append("</tbody></table>");
        }

        if (d.Bench.Count > 0)
        {
            html.Append("<table><thead><tr><th>Banc extérieur</th><th class=\"num\">Victoires</th></tr></thead><tbody>");
            foreach (var (opponent, wins, games) in d.Bench)
            {
                var pct = games == 0 ? 0 : 100.0 * wins / games;
                html.Append($"<tr><td>{E(opponent)}</td><td class=\"num\">{wins}/{games} <span class=\"bar\"><i style=\"width:{pct.ToString("0", CultureInfo.InvariantCulture)}%\"></i></span></td></tr>");
            }

            html.Append("</tbody></table>");
        }

        html.Append("</div>");

        // Ferme
        var recent = d.Trials.Where(t => (d.Now - Start(t)).TotalHours < 24).ToList();
        html.Append("<div class=\"card\"><h2>Ferme</h2>");
        html.Append(d.Current is { } cur
            ? $"<p><span class=\"dot live\"></span>Essai <b>#{cur.Id}</b> en cours depuis {Minutes(d.Now - cur.Start)}{(string.IsNullOrWhiteSpace(cur.Idea) ? "" : $" : {E(Clip(cur.Idea, 140))}")}</p>"
            : "<p><span class=\"dot\"></span>Aucun essai en cours</p>");
        var refused = recent.Count(t => t.Verdict == "NOTE_REFUSEE");
        html.Append($"<p>Dernières 24 h : <b>{recent.Count}</b> essais, <b>{recent.Count(t => t.Verdict == "GARDE")}</b> gardé(s), {recent.Count(t => t.Verdict == "ECHEC_AGENT")} échec(s) d'agent");
        html.Append(refused > 0 ? $", <span class='down'>{refused} note(s) du penseur refusée(s)</span></p>" : "</p>");
        html.Append("<p class=\"muted\">Depuis le début : ");
        html.Append(string.Join(" · ", d.Trials.GroupBy(t => t.Verdict ?? "?").OrderByDescending(g => g.Count()).Select(g => $"{E(g.Key)} {g.Count()}")));
        html.Append("</p>");
        if (d.Tokens.Trials > 0)
        {
            html.Append($"<p class=\"muted\">Tokens moyens par essai : {d.Tokens.Total.ToString("N0", Fr)} (penseur {d.Tokens.Penseur.ToString("N0", Fr)}, codeur {d.Tokens.Codeur.ToString("N0", Fr)})</p>");
        }

        html.Append(d.BlockedModels.Count == 0
            ? "<p class=\"muted\">Aucun modèle écarté</p>"
            : "<p>Modèles écartés : " + string.Join(", ", d.BlockedModels.Select(b => $"{E(b.Key)} jusqu'à {b.Value:HH:mm} UTC")) + "</p>");
        if (!string.IsNullOrWhiteSpace(d.Orientation))
        {
            html.Append($"<details><summary>Orientation actuelle du regard neuf</summary><pre>{E(d.Orientation)}</pre></details>");
        }

        html.Append("</div></section>");
    }

    /// <summary>Why the champion loses its real games, cause by cause (loss analysis).</summary>
    private static void LossesSection(StringBuilder html, StatusData d)
    {
        if (d.Losses is not { } b)
        {
            return;
        }

        html.Append("<section><h2>Pourquoi on perd</h2>");
        if (d.RealGames24h is { } recent)
        {
            // Zero for a day means the analysis is blind: it must show.
            html.Append(recent > 0
                ? $"<p class='muted'>{recent} vraies parties rejouées et mesurées ces dernières 24 h.</p>"
                : "<p><span class='down'>Aucune vraie partie mesurée depuis 24 h.</span></p>");
        }

        if (b.Games == 0)
        {
            html.Append("<p class='muted'>Aucune vraie partie du champion actuel analysée pour l'instant : la ferme rejoue ses parties classées à chaque relevé du classement.</p></section>");
            return;
        }

        html.Append($"<p class='muted'>{b.Games} vraies parties du champion analysées : {b.Won} gagnées, {b.Lost} perdues. Une cause par défaite, selon une règle fixe.</p>");
        if (b.Lost > 0)
        {
            html.Append("<table><thead><tr><th>Cause</th><th class='num'>Défaites</th></tr></thead><tbody>");
            foreach (var (cause, count) in b.Causes)
            {
                var pct = 100.0 * count / b.Lost;
                html.Append($"<tr><td>{E(Analysis.LossCauses.French(cause))}</td><td class='num'>{count} · {pct.ToString("0", Fr)} % <span class='bar'><i style='width:{pct.ToString("0", CultureInfo.InvariantCulture)}%'></i></span></td></tr>");
            }

            html.Append("</tbody></table>");
        }

        html.Append("</section>");
    }

    private static void TrialsSection(StringBuilder html, StatusData d)
    {
        html.Append("<section><h2>Derniers essais</h2><table class=\"trials\"><thead><tr><th>#</th><th>Date</th><th>Verdict</th><th class=\"num\">Duel</th><th>Idée</th></tr></thead><tbody>");
        foreach (var t in d.Trials.TakeLast(40).Reverse())
        {
            var verdict = t.Verdict ?? "?";
            var duel = t.Wins + t.Losses > 0 ? $"{t.Wins}–{t.Losses}" : "";
            html.Append($"<tr><td>{t.Id}</td><td class=\"nowrap\">{Start(t).ToString("d MMM HH:mm", Fr)}</td>");
            html.Append($"<td><span class=\"badge {VerdictClass(verdict)}\">{E(verdict)}</span></td><td class=\"num\">{duel}</td>");
            var summary = string.IsNullOrWhiteSpace(t.Idea) ? t.Reason ?? "" : t.Idea;
            html.Append($"<td><details><summary>{E(Clip(summary, 110))}</summary><p>{E(t.Idea ?? "")}</p><p class=\"muted\">{E(t.Reason ?? "")}</p></details></td></tr>");
        }

        html.Append("</tbody></table></section>");
    }

    /// <summary>Courbe du score, avec un trait à chaque nouveau champion.</summary>
    private static string ScoreChart(IReadOnlyList<RankingEntry> rankings, IReadOnlyList<ChampionEntry> champions)
    {
        const double W = 800, H = 230, L = 72, R = 12, T = 22, B = 34;
        var t0 = rankings[0].At;
        var t1 = rankings[^1].At;
        var span = Math.Max(1, (t1 - t0).TotalMinutes);
        var lo = Math.Floor(rankings.Min(r => r.Score) - 1);
        var hi = Math.Ceiling(rankings.Max(r => r.Score) + 1);
        double X(DateTime at) => L + (W - L - R) * (at - t0).TotalMinutes / span;
        double Y(double score) => T + (H - T - B) * (1 - (score - lo) / (hi - lo));
        string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        var svg = new StringBuilder();
        svg.Append($"<svg class=\"chart\" viewBox=\"0 0 {F(W)} {F(H)}\" role=\"img\" aria-label=\"Score dans le temps\">");
        for (var i = 0; i <= 4; i++)
        {
            var v = lo + (hi - lo) * i / 4;
            svg.Append($"<line class=\"grid\" x1=\"{F(L)}\" x2=\"{F(W - R)}\" y1=\"{F(Y(v))}\" y2=\"{F(Y(v))}\"/>");
            svg.Append($"<text class=\"axis\" x=\"{F(L - 6)}\" y=\"{F(Y(v) + 4)}\" text-anchor=\"end\">{v.ToString("0.#", Fr)}</text>");
        }

        foreach (var c in champions.Where(c => c.Since >= t0 && c.Since <= t1))
        {
            svg.Append($"<line class=\"champion\" x1=\"{F(X(c.Since))}\" x2=\"{F(X(c.Since))}\" y1=\"{F(T)}\" y2=\"{F(H - B)}\"><title>Nouveau champion{(c.TrialId is { } id ? $" (essai #{id})" : "")} le {c.Since.ToString("d MMM HH:mm", Fr)}</title></line>");
        }

        svg.Append("<polyline class=\"line\" points=\"");
        svg.Append(string.Join(" ", rankings.Select(r => $"{F(X(r.At))},{F(Y(r.Score))}")));
        svg.Append("\"/>");
        foreach (var r in rankings)
        {
            svg.Append($"<circle class=\"pt\" cx=\"{F(X(r.At))}\" cy=\"{F(Y(r.Score))}\" r=\"3\"><title>{r.At.ToString("d MMM HH:mm", Fr)} : score {r.Score.ToString("0.00", Fr)}, rang {r.Rank}</title></circle>");
        }

        svg.Append($"<text class=\"axis\" x=\"{F(L)}\" y=\"{F(H - 8)}\">{t0.ToString("d MMM HH:mm", Fr)}</text>");
        svg.Append($"<text class=\"axis\" x=\"{F(W - R)}\" y=\"{F(H - 8)}\" text-anchor=\"end\">{t1.ToString("d MMM HH:mm", Fr)}</text>");
        svg.Append("</svg><p class=\"muted legend\"><span class=\"key line\"></span>score · <span class=\"key champion\"></span>nouveau champion soumis</p>");
        return svg.ToString();
    }

    /// <summary>
    /// Best world rank ever held. Only fully ranked readings count: while a new submission is being
    /// ranked, its rank swings and a passing peak would become a record never really held.
    /// </summary>
    private static void RecordTile(StringBuilder html, IReadOnlyList<RankingEntry> rankings, RankingEntry last)
    {
        var best = rankings.Where(r => r.Percentage >= 100).OrderBy(r => r.Rank).ThenBy(r => r.At).FirstOrDefault();
        if (best is null)
        {
            Tile(html, "Meilleur rang", "—", "<span class=\"muted\">aucun relevé stable</span>");
            return;
        }

        var gap = last.Rank - best.Rank;
        var note = last.Percentage >= 100 && gap <= 0
            ? $"<span class=\"up\">record actuel</span> <span class=\"muted\">depuis le {best.At.ToString("d MMM", Fr)}</span>"
            : $"<span class=\"muted\">le {best.At.ToString("d MMM", Fr)} · actuel à {gap.ToString("N0", Fr)} place{(gap > 1 ? "s" : "")}</span>";
        Tile(html, "Meilleur rang", best.Rank.ToString("N0", Fr), note);
    }

    private static void Tile(StringBuilder html, string label, string value, string note) =>
        html.Append($"<div class=\"tile\"><div class=\"label\">{label}</div><div class=\"value\">{value}</div><div class=\"note\">{note}</div></div>");

    private static string Delta(double delta, string since, string unit)
    {
        var text = Math.Abs(delta).ToString(Math.Abs(delta) >= 10 ? "N0" : "0.##", Fr);
        return delta switch
        {
            > 0 => $"<span class=\"up\">▲ {text} {unit}</span> <span class=\"muted\">{since}</span>",
            < 0 => $"<span class=\"down\">▼ {text} {unit}</span> <span class=\"muted\">{since}</span>",
            _ => $"<span class=\"muted\">stable {since}</span>",
        };
    }

    private static DateTime Start(TrialSummary t) =>
        DateTime.Parse(t.Start, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string Minutes(TimeSpan s) =>
        s.TotalMinutes < 60 ? $"{(int)s.TotalMinutes} min"
        : s.TotalHours < 48 ? $"{(int)s.TotalHours} h {s.Minutes:D2}"
        : $"{(int)s.TotalDays} j {s.Hours} h";

    private static string VerdictClass(string verdict) => verdict switch
    {
        "GARDE" => "kept",
        "JETE" => "rejected",
        "ECHEC_AGENT" or "ERREUR" or "FAUTE" or "NON_COMPILE" or "NOTE_REFUSEE" => "failed",
        _ => "other",
    };

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private static string E(string s) => Encoder.Encode(s);

    private const string Css = """
        :root { --bg:#f6f7f9; --card:#fff; --text:#1c2230; --muted:#667085; --line:#e4e7ec; --accent:#2563eb;
                --good:#15803d; --bad:#b42318; --warn:#b54708; --good-bg:#dcfce7; --bad-bg:#fee4e2; --warn-bg:#fef0c7; --other-bg:#eef2f6; }
        @media (prefers-color-scheme: dark) {
          :root { --bg:#0f131a; --card:#171c25; --text:#e6e9ef; --muted:#98a2b3; --line:#2a3240; --accent:#60a5fa;
                  --good:#4ade80; --bad:#f97066; --warn:#fdb022; --good-bg:#14321f; --bad-bg:#3b1715; --warn-bg:#3a2a0c; --other-bg:#222a36; }
        }
        * { box-sizing: border-box; }
        body { margin:0; background:var(--bg); color:var(--text); font:15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
        header, main { max-width:1100px; margin:0 auto; padding:0 16px; }
        header { padding-top:24px; }
        h1 { margin:0; font-size:22px; } h1 span { color:var(--muted); font-weight:500; }
        h2 { font-size:15px; margin:0 0 12px; text-transform:uppercase; letter-spacing:.04em; color:var(--muted); }
        section { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:18px; margin:16px 0; }
        section.grid { display:grid; grid-template-columns:1fr 1fr; gap:16px; background:none; border:0; padding:0; }
        .card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:18px; min-width:0; }
        .muted { color:var(--muted); } p { margin:6px 0; }
        .tiles { display:grid; grid-template-columns:repeat(4, 1fr); gap:12px; margin-bottom:12px; }
        .tile { border:1px solid var(--line); border-radius:10px; padding:12px 14px; }
        .tile .label { color:var(--muted); font-size:13px; } .tile .value { font-size:28px; font-weight:650; font-variant-numeric:tabular-nums; }
        .tile .value small { font-size:14px; color:var(--muted); font-weight:400; } .tile .note { font-size:13px; }
        .up { color:var(--good); font-weight:600; } .down { color:var(--bad); font-weight:600; }
        .chart { width:100%; height:auto; display:block; }
        .chart .grid { stroke:var(--line); } .chart .axis { fill:var(--muted); font-size:11px; }
        .chart .line { fill:none; stroke:var(--accent); stroke-width:2.5; } .chart .pt { fill:var(--accent); }
        .chart .champion { stroke:var(--good); stroke-dasharray:4 4; stroke-width:1.5; }
        .legend { font-size:13px; } .key { display:inline-block; width:16px; height:3px; vertical-align:middle; margin:0 6px 0 0; }
        .key.line { background:var(--accent); } .key.champion { background:var(--good); }
        table { width:100%; border-collapse:collapse; } th, td { text-align:left; padding:7px 8px; border-bottom:1px solid var(--line); vertical-align:top; }
        th { font-size:12px; color:var(--muted); font-weight:600; } .num { text-align:right; font-variant-numeric:tabular-nums; white-space:nowrap; } .nowrap { white-space:nowrap; }
        .bar { display:inline-block; width:60px; height:6px; background:var(--other-bg); border-radius:3px; vertical-align:middle; margin-left:6px; overflow:hidden; }
        .bar i { display:block; height:100%; background:var(--accent); }
        .badge { font-size:12px; font-weight:600; padding:2px 8px; border-radius:999px; white-space:nowrap; }
        .badge.kept { color:var(--good); background:var(--good-bg); } .badge.rejected { color:var(--muted); background:var(--other-bg); }
        .badge.failed { color:var(--bad); background:var(--bad-bg); } .badge.other { color:var(--warn); background:var(--warn-bg); }
        details summary { cursor:pointer; } details p { margin:6px 0 0; } pre { white-space:pre-wrap; font:13px/1.45 ui-monospace, monospace; }
        .dot { display:inline-block; width:9px; height:9px; border-radius:50%; background:var(--muted); margin-right:8px; }
        .dot.live { background:var(--good); box-shadow:0 0 0 3px var(--good-bg); }
        .alert { color:var(--bad); background:var(--bad-bg); border-radius:8px; padding:8px 12px; font-weight:600; }
        code { font:13px ui-monospace, monospace; }
        td, summary { overflow-wrap:anywhere; }
        @media (max-width: 720px) {
          body { font-size:14px; }
          header { padding-top:16px; } h1 { font-size:19px; }
          section, .card { padding:14px; border-radius:10px; margin:12px 0; }
          section.grid { grid-template-columns:1fr; gap:0; }
          /* Key figures: two per row. */
          .tiles { grid-template-columns:1fr 1fr; gap:8px; }
          .tile { padding:10px 12px; } .tile .value { font-size:24px; } .tile .note { font-size:12px; }
          /* La courbe est réduite à l'écran : ses textes sont agrandis d'autant. */
          .chart .axis { font-size:24px; } .chart .line { stroke-width:4; } .chart .pt { r:5; }
          .bar { width:36px; }
          /* Essais : une fiche par essai au lieu d'un tableau. */
          .trials thead { display:none; }
          .trials tr { display:grid; grid-template-columns:auto auto 1fr; align-items:center; gap:6px 10px;
                       padding:10px 0; border-bottom:1px solid var(--line); }
          .trials td { border:0; padding:0; }
          .trials td:nth-child(2) { display:none; }
          .trials td:nth-child(4) { justify-self:end; color:var(--muted); }
          .trials td:nth-child(5) { grid-column:1 / -1; }
        }
        """;
}
