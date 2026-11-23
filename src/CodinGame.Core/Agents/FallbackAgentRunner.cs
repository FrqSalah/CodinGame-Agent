using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CodinGame.Core.Agents;

/// <summary>
/// Always uses the strongest model of a role's chain (<c>modele</c>, then <c>secours</c>). A model
/// at its limit or unavailable is set aside for a given time, recorded on disk so it survives
/// restarts, and the call moves at once to the next model; after that time the strongest is tried
/// again. An unfinished answer (cut at the output token limit, or stopped by the time limit) is
/// asked once more of the same model, then of the next one, without setting the model aside.
/// </summary>
public sealed partial class FallbackAgentRunner(IAgentRunner inner, FarmPaths paths, ILogger<FallbackAgentRunner> log) : IAgentRunner
{
    private static readonly TimeSpan DefaultBlock = TimeSpan.FromHours(5);
    private readonly Lock _gate = new();
    private IReadOnlyList<string> _available = [];

    private string BlockFile => Path.Combine(paths.DataRoot, "modeles-bloques.json");

    public async Task<AgentResult> RunAsync(AgentRequest request, CancellationToken ct)
    {
        AgentResult? last = null;
        foreach (var model in request.Settings.Chain())
        {
            if (_available.Count > 0 && !_available.Contains(model))
            {
                continue;
            }

            if (BlockedUntil(model) is { } until && until > DateTime.UtcNow)
            {
                continue;
            }

            var attempt = request with { Settings = request.Settings with { Modele = model } };
            var result = await inner.RunAsync(attempt, ct);
            if (!result.Success && IsOverrun(result.Error))
            {
                // A cut answer is usually a one-off (the thinker on High, about one run in six, writes
                // tens of thousands of characters instead of its note): the same model is asked again.
                log.LogWarning("Modèle {Modele} : réponse inachevée ({Raison}), appel relancé une fois avec le même modèle",
                    model, result.Error);
                result = await inner.RunAsync(attempt, ct);
            }

            if (!result.Success && IsOverrun(result.Error))
            {
                // Cut twice: the model stays usable, but this call moves to the next one, without blocking it.
                log.LogWarning("Modèle {Modele} : réponse encore inachevée ({Raison}), appel retenté avec le suivant de la chaîne",
                    model, result.Error);
                last = result;
                continue;
            }

            if (result.Success || !IsExhausted(result.Error))
            {
                if (model != request.Settings.Modele)
                {
                    log.LogInformation("Agent {Role} : modèle de secours {Modele} utilisé", request.Role, model);
                }

                return result with { Model = model };
            }

            var blockedUntil = DateTime.UtcNow + (request.BlockFor ?? DefaultBlock);
            Block(model, blockedUntil);
            log.LogWarning("Modèle {Modele} indisponible ({Raison}) : écarté jusqu'à {Jusqua:HH:mm} UTC, passage au suivant",
                model, result.Error, blockedUntil);
            last = result;
        }

        var blocked = string.Join(", ", Blocked().Select(b => $"{b.Key} jusqu'à {b.Value:HH:mm} UTC"));
        return last is null
            ? new AgentResult(false, "", $"aucun modèle utilisable dans la chaîne ({blocked})", 0, 0)
            : last with { Error = $"aucun modèle de la chaîne n'a abouti (écartés : {(blocked.Length > 0 ? blocked : "aucun")}) ; dernière erreur : {last.Error}" };
    }

    public async Task<AgentStatus> CheckAsync(CancellationToken ct)
    {
        var status = await inner.CheckAsync(ct);
        if (status.Availability == AgentAvailability.Ready)
        {
            _available = status.Models;
        }

        return status;
    }

    public Task GrantAsync(string directory, CancellationToken ct) => inner.GrantAsync(directory, ct);

    /// <summary>Modèles actuellement écartés, avec la fin de leur blocage.</summary>
    public IReadOnlyDictionary<string, DateTime> Blocked()
    {
        lock (_gate)
        {
            return Read().Where(b => b.Value > DateTime.UtcNow).ToDictionary();
        }
    }

    /// <summary>
    /// L'erreur dit-elle que le modèle est à sa limite ou indisponible ? Un dépassement de la limite
    /// de tokens d'une seule réponse n'en est pas un : le modèle reste utilisable.
    /// </summary>
    internal static bool IsExhausted(string? error) =>
        error is not null && !IsOverrun(error) && Exhausted().IsMatch(error);

    /// <summary>
    /// L'erreur dit-elle que la réponse est inachevée : coupée à la limite de tokens de sortie, ou
    /// arrêtée par le délai ? Le modèle n'est pas en cause durablement ; seul cet appel change de modèle.
    /// </summary>
    internal static bool IsOverrun(string? error) => error is not null && Overrun().IsMatch(error);

    private DateTime? BlockedUntil(string model)
    {
        lock (_gate)
        {
            return Read().TryGetValue(model, out var until) ? until : null;
        }
    }

    private void Block(string model, DateTime until)
    {
        lock (_gate)
        {
            var all = Read().Where(b => b.Value > DateTime.UtcNow).ToDictionary();
            all[model] = until;
            Directory.CreateDirectory(Path.GetDirectoryName(BlockFile)!);
            File.WriteAllText(BlockFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private Dictionary<string, DateTime> Read()
    {
        try
        {
            return File.Exists(BlockFile)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(BlockFile)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"quota|exhaust|rate.?limit|too many requests|\b429\b|resource.?exhausted|capacity|overload|unavailable|not available|unknown model|invalid model|credits|usage limit|limit reached|try again later",
        RegexOptions.IgnoreCase)]
    private static partial Regex Exhausted();

    [GeneratedRegex(@"output token|cut off|print timeout|response is partial|durée maximale dépassée", RegexOptions.IgnoreCase)]
    private static partial Regex Overrun();
}
