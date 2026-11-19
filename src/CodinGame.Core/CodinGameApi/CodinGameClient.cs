using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace CodinGame.Core.CodinGameApi;

public class CodinGameException(string message) : Exception(message);

/// <summary>Le token est absent ou refusé : il faut le renouveler, ne jamais réessayer.</summary>
public sealed class TokenRejectedException(string message) : CodinGameException(message);

/// <summary>Une limite de l'API est atteinte pour longtemps : il faut attendre <see cref="RetryAfter"/>.</summary>
public sealed class QuotaExhaustedException(string message, TimeSpan retryAfter) : CodinGameException(message)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

public sealed record PuzzleInfo(string League, int PlayersMin, int PlayersMax, IReadOnlyList<string> Languages, string Statement);

public sealed record TestGameResult(GameLog? Log, string? Failure);

/// <summary>Place du bot soumis : rang mondial, score, ligue et rang dans la ligue.</summary>
public sealed record RankingSnapshot(int Rank, int Total, double Score, string League, int LeagueRank, int LeagueTotal, int Percentage, long? SubmissionId = null);

/// <summary>
/// Seul point d'accès à l'API publique CodinGame. Le token est relu à chaque appel et
/// n'apparaît jamais dans un journal ni une erreur. Une seule partie de test à la fois ;
/// les limites sont lues dans chaque réponse, jamais supposées. La soumission au classement
/// n'est appelée que si la configuration l'autorise (<c>api.soumettre_champions</c>).
/// </summary>
public sealed class CodinGameClient(HttpClient http, string tokenFile, ILogger<CodinGameClient> log)
{
    public const string BaseUrl = "https://www.codingame.com/api/v1";

    /// <summary>Au-delà de ce délai d'attente, on rend la main au lieu de bloquer.</summary>
    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromMinutes(16);

    private static readonly SemaphoreSlim TestGameGate = new(1, 1);

    public async Task<PuzzleInfo> GetPuzzleAsync(string puzzle, CancellationToken ct)
    {
        var p = await SendAsync(HttpMethod.Get, $"/puzzles/{puzzle}", null, ct);
        return new PuzzleInfo(
            p.TryGetProperty("league", out var l) ? l.GetString() ?? "" : "",
            p.GetProperty("players_min").GetInt32(),
            p.GetProperty("players_max").GetInt32(),
            p.TryGetProperty("languages", out var langs) ? langs.EnumerateArray().Select(x => x.GetProperty("id").GetString() ?? "").ToArray() : [],
            p.TryGetProperty("statement", out var s) ? s.GetString() ?? "" : "");
    }

    /// <summary>Identifiants des dernières parties classées du bot soumis (pour y relever des cartes réelles).</summary>
    public async Task<IReadOnlyList<long>> GetBattleGameIdsAsync(string puzzle, int max, CancellationToken ct)
    {
        var ids = new List<long>();
        string? before = null;
        while (ids.Count < max)
        {
            var query = $"/puzzles/{puzzle}/battles?limit=100" + (before is null ? "" : $"&before={before}");
            var page = await SendAsync(HttpMethod.Get, query, null, ct);
            if (!page.TryGetProperty("battles", out var battles))
            {
                break;
            }

            ids.AddRange(battles.EnumerateArray()
                .Where(b => b.TryGetProperty("done", out var d) && d.GetBoolean())
                .Select(b => b.GetProperty("game_id").GetInt64()));
            if (!page.TryGetProperty("next_before", out var next))
            {
                break;
            }

            before = next.ToString();
        }

        return ids.Take(max).ToList();
    }

    public async Task<GameLog> GetGameAsync(long gameId, CancellationToken ct) =>
        GameLog.Parse(await SendAsync(HttpMethod.Get, $"/games/{gameId}?format=json", null, ct));

    /// <summary>Joue une partie de test et attend sa fin. <paramref name="options"/> rejoue une carte précise.</summary>
    public async Task<TestGameResult> PlayTestGameAsync(
        string puzzle, string language, string code, string opponent, string? options, CancellationToken ct)
    {
        await TestGameGate.WaitAsync(ct);
        try
        {
            await PaceTestGamesAsync(ct);
            var body = new JsonObject
            {
                ["language"] = language,
                ["code"] = code,
                ["opponents"] = new JsonArray(long.TryParse(opponent, out var agentId) ? JsonValue.Create(agentId) : JsonValue.Create(opponent)),
            };
            if (!string.IsNullOrEmpty(options))
            {
                body["game_options"] = options;
            }

            var r = await SendAsync(HttpMethod.Post, $"/puzzles/{puzzle}/test-games?wait=30&format=json", body, ct);
            while (r.GetProperty("status").GetString() == "running")
            {
                var poll = r.GetProperty("poll_url").GetString()!.Replace("/api/v1", "");
                r = await SendAsync(HttpMethod.Get, poll + "?wait=30&format=json", null, ct);
            }

            Telemetry.ApiGames.Add(1);
            return r.GetProperty("status").GetString() == "done"
                ? new TestGameResult(GameLog.Parse(r.GetProperty("log")), null)
                : new TestGameResult(null, Text(r, "failure") ?? "échec sans détail");
        }
        finally
        {
            TestGameGate.Release();
        }
    }

    /// <summary>Soumet un bot au classement. Peut faire changer de ligue, donc de règles.</summary>
    public async Task<long> SubmitAsync(string puzzle, string language, string code, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Post, $"/puzzles/{puzzle}/submissions",
            new JsonObject { ["language"] = language, ["code"] = code }, ct);
        return r.GetProperty("submission_id").GetInt64();
    }

    /// <summary>Joueurs de notre ligue contre qui des parties de test sont permises, avec leur rang.</summary>
    public async Task<IReadOnlyList<(string AgentId, int Rank)>> GetOpponentsAsync(string puzzle, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, $"/puzzles/{puzzle}/opponents", null, ct);
        return r.TryGetProperty("opponents", out var list)
            ? list.EnumerateArray()
                .Where(o => o.TryGetProperty("rank", out var rank) && rank.ValueKind == JsonValueKind.Number)
                .Select(o => (o.GetProperty("agent_id").ToString(), o.GetProperty("rank").GetInt32()))
                .ToList()
            : [];
    }

    /// <summary>Classement actuel du bot soumis, ou null s'il n'y en a pas encore.</summary>
    public async Task<RankingSnapshot?> GetRankingAsync(string puzzle, CancellationToken ct)
    {
        var p = await SendAsync(HttpMethod.Get, $"/puzzles/{puzzle}/progress", null, ct);
        if (!p.TryGetProperty("rank", out var rank) || rank.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        int Int(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        return new RankingSnapshot(
            rank.GetInt32(), Int("total"),
            p.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0,
            Text(p, "league") ?? "", Int("league_rank"), Int("league_total"), Int("percentage"),
            p.TryGetProperty("submission_id", out var sub) && sub.ValueKind == JsonValueKind.Number ? sub.GetInt64() : null);
    }

    /// <summary>
    /// Why <paramref name="needed"/> test games cannot be played now, or null if they can. Only the
    /// windows large enough to hold them count: the short ones (10 per minute...) refill while the
    /// games are played, and <see cref="PlayTestGameAsync"/> paces itself on them.
    /// </summary>
    public async Task<string?> TestGameShortfallAsync(string puzzle, int needed, CancellationToken ct) =>
        TestGameShortfall(await TestGameWindowsAsync(puzzle, ct), needed);

    /// <summary>Every test game window of the puzzle: name, limit and games left.</summary>
    public async Task<IReadOnlyList<(string Name, int Limit, int Remaining)>> TestGameWindowsAsync(string puzzle, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, $"/limits?puzzle={puzzle}", null, ct);
        return r.TryGetProperty("test_games", out var windows) && windows.ValueKind == JsonValueKind.Array
            ? windows.EnumerateArray().Select(w => (
                w.GetProperty("name").GetString() ?? "?", w.GetProperty("limit").GetInt32(), w.GetProperty("remaining").GetInt32())).ToList()
            : [];
    }

    /// <summary>The first window whose limit can hold <paramref name="needed"/> games but whose remainder cannot.</summary>
    public static string? TestGameShortfall(IEnumerable<(string Name, int Limit, int Remaining)> windows, int needed) =>
        windows.Where(w => w.Limit >= needed && w.Remaining < needed)
            .Select(w => $"{w.Remaining} test games left in window {w.Name}, {needed} needed")
            .FirstOrDefault();

    /// <summary>Until when a test game window is empty, read from the last responses: test games wait for it instead of hitting 429.</summary>
    private DateTime _testGamesBlockedUntil = DateTime.MinValue;

    private void RememberEmptyTestGameWindows(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("limits", out var limits)
            || limits.ValueKind != JsonValueKind.Object || !limits.TryGetProperty("test_games", out var tg))
        {
            return;
        }

        var windows = tg.ValueKind == JsonValueKind.Array ? tg.EnumerateArray().ToList() : tg.ValueKind == JsonValueKind.Object ? [tg] : [];
        foreach (var w in windows)
        {
            if (w.TryGetProperty("remaining", out var rem) && rem.ValueKind == JsonValueKind.Number && rem.GetInt32() <= 0
                && w.TryGetProperty("resets_in_seconds", out var reset) && reset.ValueKind == JsonValueKind.Number)
            {
                var until = DateTime.UtcNow.AddSeconds(reset.GetDouble() + 1);
                if (until > _testGamesBlockedUntil)
                {
                    _testGamesBlockedUntil = until;
                }
            }
        }
    }

    /// <summary>Waits for an empty test game window to refill; a wait longer than <see cref="MaxPacingWait"/> is a quota pause.</summary>
    private async Task PaceTestGamesAsync(CancellationToken ct)
    {
        var wait = _testGamesBlockedUntil - DateTime.UtcNow;
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        if (wait > MaxPacingWait)
        {
            throw new QuotaExhaustedException("Fenêtre de parties de test CodinGame épuisée.", wait);
        }

        log.LogInformation("Parties de test : fenêtre épuisée, attente de {Seconds} s", (int)wait.TotalSeconds);
        await Task.Delay(wait, ct);
    }

    /// <summary>Longest pause between two test games of the same confirmation (the 15-minute window).</summary>
    private static readonly TimeSpan MaxPacingWait = TimeSpan.FromMinutes(16);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, BaseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadToken());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();

            if (response.IsSuccessStatusCode)
            {
                if (json.ValueKind == JsonValueKind.Object && MinRemaining(json, "test_games") is { } left)
                {
                    Telemetry.SetApiTestGamesLeft(left);
                }

                RememberEmptyTestGameWindows(json);
                return json;
            }

            var problem = Describe(response.StatusCode, json);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new TokenRejectedException($"Token CodinGame refusé ({problem}). Il faut en obtenir un nouveau.");

                case HttpStatusCode.TooManyRequests:
                    var wait = RetryAfter(response, json);
                    if (wait > MaxInlineWait || attempt >= 3)
                    {
                        throw new QuotaExhaustedException($"Limite CodinGame atteinte ({problem}).", wait);
                    }

                    log.LogInformation("Limite CodinGame : attente de {Seconds} s ({Problem})", (int)wait.TotalSeconds, problem);
                    await Task.Delay(wait, ct);
                    continue;

                case HttpStatusCode.Conflict when json.ValueKind == JsonValueKind.Object && json.TryGetProperty("poll_url", out var running) && attempt < 5:
                    log.LogInformation("Une partie de test est déjà en cours : attente de sa fin");
                    await SendAsync(HttpMethod.Get, running.GetString()!.Replace("/api/v1", "") + "?wait=30", null, ct);
                    continue;

                default:
                    throw new CodinGameException($"{method} {path} : {problem}");
            }
        }
    }

    private string ReadToken()
    {
        if (!File.Exists(tokenFile))
        {
            throw new TokenRejectedException($"Token CodinGame absent ({tokenFile}).");
        }

        return File.ReadAllText(tokenFile).Trim();
    }

    private static string Describe(HttpStatusCode status, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            return $"HTTP {(int)status}";
        }

        var parts = new[] { Text(json, "title"), Text(json, "detail"), Text(json, "code"), Text(json, "hint") }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return $"HTTP {(int)status} : {string.Join(" — ", parts)}";
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response, JsonElement json)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("retry_after_seconds", out var s) && s.TryGetDouble(out var sec))
        {
            return TimeSpan.FromSeconds(sec);
        }

        return TimeSpan.FromSeconds(60);
    }

    private static int? MinRemaining(JsonElement json, string kind)
    {
        if (!json.TryGetProperty("limits", out var limits))
        {
            return null;
        }

        var windows = limits.ValueKind == JsonValueKind.Object && limits.TryGetProperty(kind, out var w) ? w
                    : json.TryGetProperty(kind, out var top) ? top : default;
        if (windows.ValueKind == JsonValueKind.Object)
        {
            return windows.GetProperty("remaining").GetInt32();
        }

        return windows.ValueKind == JsonValueKind.Array
            ? windows.EnumerateArray().Select(x => x.GetProperty("remaining").GetInt32()).DefaultIfEmpty().Min()
            : null;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;
}
