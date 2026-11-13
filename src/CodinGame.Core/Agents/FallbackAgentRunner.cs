using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CodinGame.Core.Agents;

/// <summary>
/// Utilise toujours le modèle le plus fort de la chaîne d'un rôle (<c>modele</c>, puis
/// <c>secours</c>). Quand un modèle atteint sa limite ou est indisponible, il est bloqué pour une
/// durée donnée — blocage écrit sur disque, donc conservé aux redémarrages — et l'appel repart
/// aussitôt avec le modèle suivant. Une fois le délai écoulé, le plus fort est réessayé.
/// </summary>
public sealed partial class FallbackAgentRunner(AntigravityRunner inner, FarmPaths paths, ILogger<FallbackAgentRunner> log) : IAgentRunner
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

            var result = await inner.RunAsync(request with { Settings = request.Settings with { Modele = model } }, ct);
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
            : last with { Error = $"tous les modèles de la chaîne sont à leur limite ({blocked}) ; dernière erreur : {last.Error}" };
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
        error is not null && !error.Contains("output token", StringComparison.OrdinalIgnoreCase) && Exhausted().IsMatch(error);

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
}
