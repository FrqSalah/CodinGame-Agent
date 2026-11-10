using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodinGame.Core.Config;
using Microsoft.Extensions.Logging;

namespace CodinGame.Core.Agents;

public sealed record AgentRequest(string Role, AgentRole Settings, string WorkDirectory, string Prompt);

public sealed record AgentResult(bool Success, string Response, string? Error, double Seconds, long Tokens);

public enum AgentAvailability
{
    Ready,
    NotLoggedIn,
    Missing,
}

public sealed record AgentStatus(AgentAvailability Availability, IReadOnlyList<string> Models, string Detail);

/// <summary>Seule voie pour lancer un outil d'IA.</summary>
public interface IAgentRunner
{
    Task<AgentResult> RunAsync(AgentRequest request, CancellationToken ct);

    Task<AgentStatus> CheckAsync(CancellationToken ct);

    /// <summary>Rend un dossier de travail accessible à l'utilisateur de l'agent.</summary>
    Task GrantAsync(string directory, CancellationToken ct);
}

/// <summary>
/// Antigravity CLI (<c>agy</c>) en mode sans écran. Trois règles :
/// le code de sortie ne prouve rien (seul le statut JSON compte, et l'appelant vérifie
/// ce qui a été produit) ; la durée est bornée par ce programme, qui tue le processus ;
/// avec <c>FARM_AGENT_USER</c>, l'agent tourne sous un autre utilisateur, qui ne peut
/// pas lire le token CodinGame.
/// </summary>
public sealed partial class AntigravityRunner(ILogger<AntigravityRunner> log) : IAgentRunner
{
    private readonly string? _agentUser = Environment.GetEnvironmentVariable("FARM_AGENT_USER");
    private readonly string? _agentDbus = Environment.GetEnvironmentVariable("FARM_AGENT_DBUS");

    public async Task<AgentResult> RunAsync(AgentRequest request, CancellationToken ct)
    {
        var minutes = request.Settings.MinutesMax;
        string[] args =
        [
            "-p", request.Prompt,
            "--model", request.Settings.Modele,
            "--output-format", "json",
            "--mode", "accept-edits",
            "--print-timeout", $"{minutes}m",
        ];

        using var activity = Telemetry.Source.StartActivity($"agent.{request.Role}");
        activity?.SetTag("agent.role", request.Role);
        activity?.SetTag("agent.modele", request.Settings.Modele);

        var started = Stopwatch.StartNew();
        var (exit, stdout, stderr, killed) = await RunProcessAsync(args, request.WorkDirectory, TimeSpan.FromMinutes(minutes + 2), ct);
        var seconds = started.Elapsed.TotalSeconds;
        Telemetry.AgentSeconds.Record(seconds, new KeyValuePair<string, object?>("role", request.Role));

        if (killed)
        {
            return new AgentResult(false, "", $"durée maximale dépassée ({minutes} min) : processus arrêté", seconds, 0);
        }

        var envelope = ParseEnvelope(stdout);
        if (envelope is null)
        {
            return new AgentResult(false, "", $"sortie illisible (code {exit}) : {Tail(stderr)}", seconds, 0);
        }

        var status = (string?)envelope["status"];
        var response = (string?)envelope["response"] ?? "";
        var tokens = (long?)envelope["usage"]?["total_tokens"] ?? 0;
        Telemetry.AgentTokens.Add(tokens, new KeyValuePair<string, object?>("role", request.Role));
        activity?.SetTag("agent.tokens", tokens);
        activity?.SetTag("agent.statut", status);

        if (status != "SUCCESS")
        {
            var error = (string?)envelope["error"] ?? Tail(stderr);
            return new AgentResult(false, response, $"statut {status} : {error}", seconds, tokens);
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            log.LogDebug("Antigravity ({Role}) a écrit sur sa sortie d'erreur : {Stderr}", request.Role, Tail(stderr));
        }

        return new AgentResult(true, response, null, seconds, tokens);
    }

    public async Task<AgentStatus> CheckAsync(CancellationToken ct)
    {
        try
        {
            var (_, stdout, stderr, killed) = await RunProcessAsync(["models"], Path.GetTempPath(), TimeSpan.FromMinutes(1), ct);
            var all = stdout + "\n" + stderr;
            if (killed)
            {
                return new AgentStatus(AgentAvailability.Missing, [], "agy models n'a pas répondu en une minute");
            }

            if (all.Contains("sign in", StringComparison.OrdinalIgnoreCase) || all.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            {
                return new AgentStatus(AgentAvailability.NotLoggedIn, [], "Antigravity n'est pas connecté");
            }

            var models = stdout.Split('\n')
                .Select(l => ModelLine().Match(l))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();
            return new AgentStatus(AgentAvailability.Ready, models, $"{models.Count} modèles disponibles");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new AgentStatus(AgentAvailability.Missing, [], $"agy introuvable : {ex.Message}");
        }
    }

    public async Task GrantAsync(string directory, CancellationToken ct)
    {
        if (_agentUser is null)
        {
            return;
        }

        var info = new ProcessStartInfo("chown") { UseShellExecute = false };
        foreach (var a in new[] { "-R", $"{_agentUser}:{_agentUser}", directory })
        {
            info.ArgumentList.Add(a);
        }

        using var p = Process.Start(info)!;
        await p.WaitForExitAsync(ct);
    }

    /// <summary>
    /// Interdit à l'agent les commandes et le réseau, et l'accès hors de son dossier.
    /// À appeler au démarrage : le fichier de réglages d'Antigravity est fusionné, pas remplacé.
    /// </summary>
    public static void EnsurePermissions(string agentHome)
    {
        var path = Path.Combine(agentHome, ".gemini", "antigravity-cli", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var settings = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o ? o : [];
        settings["allowNonWorkspaceAccess"] = false;
        var permissions = settings["permissions"] as JsonObject ?? [];
        var deny = permissions["deny"] as JsonArray ?? [];
        foreach (var rule in new[] { "command(*)", "read_url(*)", "execute_url(*)" })
        {
            if (!deny.Any(d => (string?)d == rule))
            {
                deny.Add(rule);
            }
        }

        permissions["deny"] = deny;
        settings["permissions"] = permissions;
        File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<(int Exit, string Stdout, string Stderr, bool Killed)> RunProcessAsync(
        string[] agyArgs, string workDir, TimeSpan limit, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (_agentUser is not null)
        {
            // L'agent ne voit que son propre environnement : rien des variables de la ferme.
            info.FileName = "setpriv";
            foreach (var a in new[] { $"--reuid={_agentUser}", $"--regid={_agentUser}", "--init-groups", "--", "agy" })
            {
                info.ArgumentList.Add(a);
            }

            info.Environment.Clear();
            info.Environment["HOME"] = $"/home/{_agentUser}";
            info.Environment["USER"] = _agentUser;
            info.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
            info.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
            if (_agentDbus is not null)
            {
                info.Environment["DBUS_SESSION_BUS_ADDRESS"] = _agentDbus;
            }
        }
        else
        {
            info.FileName = "agy";
        }

        foreach (var a in agyArgs)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Impossible de lancer agy.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        var killed = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            killed = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        return (process.ExitCode, await stdout, await stderr, killed);
    }

    /// <summary>La sortie JSON est la dernière ligne qui commence par « { ».</summary>
    private static JsonObject? ParseEnvelope(string stdout)
    {
        foreach (var line in stdout.Split('\n').Reverse())
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    return JsonNode.Parse(trimmed) as JsonObject;
                }
                catch (JsonException)
                {
                    // Ligne qui n'est pas l'enveloppe : on continue.
                }
            }
        }

        // Enveloppe sur plusieurs lignes : on tente depuis la première accolade.
        var start = stdout.IndexOf('{');
        if (start >= 0)
        {
            try
            {
                return JsonNode.Parse(stdout[start..]) as JsonObject;
            }
            catch (JsonException)
            {
                // Pas de JSON exploitable.
            }
        }

        return null;
    }

    private static string Tail(string s) => s.Length <= 500 ? s.Trim() : "…" + s[^500..].Trim();

    [GeneratedRegex(@"^\s*([a-z0-9][a-z0-9.\-]*[a-z0-9])\s{2,}\S")]
    private static partial Regex ModelLine();
}
