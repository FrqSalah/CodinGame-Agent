using System.Diagnostics;

namespace CodinGame.Core.Arena;

/// <summary>Un bot compilé, lancé comme processus et piloté par son entrée et sa sortie standard.</summary>
public sealed class BotProcess : IBotConnection, IAsyncDisposable
{
    private readonly Process _process;
    private Task<string?>? _pendingRead;

    private BotProcess(Process process) => _process = process;

    public static BotProcess Start(string executable)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Impossible de lancer {executable}.");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return new BotProcess(process);
    }

    public async Task SendAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        try
        {
            foreach (var line in lines)
            {
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            }

            await _process.StandardInput.FlushAsync(ct);
        }
        catch (IOException)
        {
            // Le bot s'est arrêté : la lecture suivante le constatera.
        }
    }

    public async Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken ct)
    {
        // Une lecture qui a expiré reste en cours : on la reprend plutôt que d'en lancer une seconde.
        _pendingRead ??= _process.StandardOutput.ReadLineAsync(ct).AsTask();
        var finished = await Task.WhenAny(_pendingRead, Task.Delay(timeout, ct));
        if (finished != _pendingRead)
        {
            return null;
        }

        var line = await _pendingRead;
        _pendingRead = null;
        return line;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            // Déjà terminé ou impossible à tuer : rien de plus à faire.
        }

        _process.Dispose();
    }
}
