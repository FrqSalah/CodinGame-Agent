using System.Diagnostics;

namespace CodinGame.Core.Git;

/// <summary>
/// Gestionnaire de versions automatisé. Chaque jeu possède son propre sous-dépôt Git local.
///
/// RÈGLE DE SÉCURITÉ ABSOLUE : cette classe n'exécute jamais `git reset --hard`,
/// sous aucune condition. Annuler une expérience, c'est supprimer sa branche — rien d'autre.
/// `main` n'est modifiée que par un merge de promotion.
///
/// Voir .Agent/DETERMINISTIC_LAYER.md §2.B.
/// </summary>
public sealed class GitRepositoryManager
{
    private readonly string _repoPath;

    public GitRepositoryManager(string repoPath)
    {
        _repoPath = repoPath;
    }

    /// <summary>
    /// Initialise un nouveau dépôt Git si nécessaire.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(Path.Combine(_repoPath, ".git")))
        {
            await RunGitAsync("init", ct);
            // Créer un commit initial vide pour que main existe
            await RunGitAsync("commit --allow-empty -m \"Initial commit\"", ct);
        }
    }

    /// <summary>
    /// Crée la branche isolée experiment/&lt;id&gt; à partir de main.
    /// </summary>
    public async Task CreateExperimentBranchAsync(string experimentId, CancellationToken ct = default)
    {
        await RunGitAsync($"checkout -b experiment/{experimentId} main", ct);
    }

    /// <summary>
    /// Commit horodaté sur la branche d'expérience.
    /// Le message est lisible par un humain et ne contient aucune métrique :
    /// les chiffres vont dans l'ExperimentStore (source de vérité unique).
    /// </summary>
    public async Task CommitExperimentAsync(string experimentId, string message, CancellationToken ct = default)
    {
        await RunGitAsync($"checkout experiment/{experimentId}", ct);
        await RunGitAsync("add -A", ct);
        await RunGitAsync($"commit -m \"{message}\"", ct);
    }

    /// <summary>
    /// Merge de experiment/&lt;id&gt; sur main, puis création du tag exp/&lt;experimentId&gt;.
    /// Le tag ne contient aucune valeur de performance (voir DETERMINISTIC_LAYER.md §2.B).
    /// </summary>
    public async Task PromoteExperimentAsync(string experimentId, CancellationToken ct = default)
    {
        await RunGitAsync("checkout main", ct);
        await RunGitAsync($"merge experiment/{experimentId} --no-ff -m \"Promote experiment/{experimentId}\"", ct);
        await RunGitAsync($"tag exp/{experimentId}", ct);
        await RunGitAsync($"branch -d experiment/{experimentId}", ct);
    }

    /// <summary>
    /// Suppression de la branche experiment/&lt;id&gt;. C'est l'unique mécanisme de rollback.
    /// Opération idempotente, sans effet sur main.
    /// </summary>
    public async Task AbandonExperimentAsync(string experimentId, CancellationToken ct = default)
    {
        await RunGitAsync("checkout main", ct);
        await RunGitAsync($"branch -D experiment/{experimentId}", ct);
    }

    /// <summary>Hash du commit courant.</summary>
    public async Task<string> GetHeadHashAsync(CancellationToken ct = default)
    {
        return (await RunGitAsync("rev-parse HEAD", ct)).Trim();
    }

    private async Task<string> RunGitAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = _repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {arguments} failed (exit {process.ExitCode}): {stderr}");

        return stdout;
    }
}
