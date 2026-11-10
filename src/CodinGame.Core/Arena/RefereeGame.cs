using System.Diagnostics;
using System.Globalization;

namespace CodinGame.Core.Arena;

/// <summary>Une connexion ligne à ligne vers un bot (processus réel ou faux bot de test).</summary>
public interface IBotConnection
{
    Task SendAsync(IReadOnlyList<string> lines, CancellationToken ct);

    /// <summary>Lit une ligne ; <c>null</c> si le bot ne répond pas à temps ou s'est arrêté.</summary>
    Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken ct);
}

public sealed record TurnLimits(TimeSpan FirstTurn, TimeSpan Turn);

public enum FaultKind
{
    None,
    Timeout,
    InvalidOutput,
}

/// <summary>Résultat d'une partie. <see cref="Winner"/> vaut -1 en cas d'égalité.</summary>
public sealed record MatchResult(int Winner, IReadOnlyList<FaultKind> Faults, string? Detail = null)
{
    public bool IsDraw => Winner < 0;
}

/// <summary>
/// Fait jouer deux bots avec un moteur (« referee ») existant, au protocole de
/// <c>cg-brutaltester</c> (mode historique) : le moteur écrit <c>###Input p</c> suivi des
/// lignes destinées au joueur p, <c>###Output p n</c> pour réclamer n lignes de réponse,
/// et <c>###End gagnant perdant</c> à la fin. Ce programme ne simule rien : il relaie,
/// et il applique les limites de temps que le moteur n'applique pas.
/// </summary>
public sealed class RefereeGame(string refereeCommand)
{
    public async Task<MatchResult> PlayAsync(IReadOnlyList<IBotConnection> bots, long seed, TurnLimits limits, CancellationToken ct)
    {
        var parts = refereeCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var info = new ProcessStartInfo(parts[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in parts.Skip(1))
        {
            info.ArgumentList.Add(arg);
        }

        using var referee = Process.Start(info) ?? throw new InvalidOperationException($"Moteur introuvable : {parts[0]}");
        var errors = new List<string>();
        referee.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (errors)
                {
                    errors.Add(e.Data);
                }
            }
        };
        referee.BeginErrorReadLine();
        try
        {
            await referee.StandardInput.WriteLineAsync($"###Seed {seed.ToString(CultureInfo.InvariantCulture)}".AsMemory(), ct);
            await referee.StandardInput.WriteLineAsync("###Start 2".AsMemory(), ct);
            await referee.StandardInput.FlushAsync(ct);
            return await RelayAsync(referee, errors, bots, limits, ct);
        }
        finally
        {
            if (!referee.HasExited)
            {
                referee.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task<MatchResult> RelayAsync(
        Process referee, List<string> errors, IReadOnlyList<IBotConnection> bots, TurnLimits limits, CancellationToken ct)
    {
        var faults = new[] { FaultKind.None, FaultKind.None };
        var answered = new bool[2];
        var pending = new[] { new List<string>(), new List<string>() };
        var target = -1;

        while (true)
        {
            var line = await referee.StandardOutput.ReadLineAsync(ct)
                       ?? throw new InvalidOperationException("Le moteur s'est arrêté sans annoncer de fin de partie.");
            if (!line.StartsWith("###", StringComparison.Ordinal))
            {
                if (target < 0)
                {
                    throw new InvalidOperationException($"Ligne du moteur hors d'un bloc ###Input : « {line} »");
                }

                pending[target].Add(line);
                continue;
            }

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (words[0])
            {
                case "###Input":
                    target = Player(words, 1);
                    break;

                case "###Output":
                    var player = Player(words, 1);
                    var count = int.Parse(words[2], CultureInfo.InvariantCulture);
                    target = -1;
                    await bots[player].SendAsync(pending[player], ct);
                    pending[player].Clear();

                    var limit = answered[player] ? limits.Turn : limits.FirstTurn;
                    answered[player] = true;
                    var replies = new List<string>(count);
                    for (var i = 0; i < count; i++)
                    {
                        var reply = await bots[player].ReadLineAsync(limit, ct);
                        if (reply is null)
                        {
                            faults[player] = FaultKind.Timeout;
                            return new MatchResult(1 - player, faults, $"joueur {player} : pas de réponse à temps");
                        }

                        replies.Add(reply);
                    }

                    foreach (var reply in replies)
                    {
                        await referee.StandardInput.WriteLineAsync(reply.AsMemory(), ct);
                    }

                    await referee.StandardInput.FlushAsync(ct);
                    break;

                case "###End":
                    // « ###End gagnant perdant » ; deux joueurs à égalité partagent un rang : « ###End 01 ».
                    if (words.Length < 3)
                    {
                        return new MatchResult(-1, faults, "égalité");
                    }

                    var winner = Player(words, 1);
                    // Le moteur signale une sortie invalide sur sa sortie d'erreur, juste avant de s'arrêter.
                    await referee.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(2), ct).ContinueWith(_ => { }, CancellationToken.None);
                    bool invalid;
                    lock (errors)
                    {
                        invalid = errors.Any(e => e.Contains("INVALID", StringComparison.OrdinalIgnoreCase));
                    }

                    if (!invalid)
                    {
                        return new MatchResult(winner, faults);
                    }

                    faults[1 - winner] = FaultKind.InvalidOutput;
                    return new MatchResult(winner, faults, $"joueur {1 - winner} : sortie invalide");

                default:
                    throw new InvalidOperationException($"Commande du moteur inconnue : « {line} »");
            }
        }
    }

    private static int Player(string[] words, int index) =>
        words.Length > index && int.TryParse(words[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is 0 or 1
            ? p
            : throw new InvalidOperationException($"Numéro de joueur illisible : « {string.Join(' ', words)} »");
}
