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
    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromMinutes(2);

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

    /// <summary>Parties de test restantes sur la fenêtre la plus contraignante, ou null si inconnu.</summary>
    public async Task<int?> TestGamesLeftAsync(string puzzle, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, $"/limits?puzzle={puzzle}", null, ct);
        return MinRemaining(r, "test_games");
    }

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
