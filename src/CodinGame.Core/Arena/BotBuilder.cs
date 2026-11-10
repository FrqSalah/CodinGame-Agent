using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodinGame.Core.Arena;

public sealed record BuildResult(bool Success, string? Executable, string Errors);

/// <summary>
/// Compile le source d'un bot avec la commande de la configuration, et garde
/// l'exécutable en cache selon l'empreinte du source.
/// </summary>
public sealed class BotBuilder(string cacheDirectory, string commandTemplate, string sourceFileName)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Empreinte stable d'un source : identifie un bot partout dans le journal.</summary>
    public static string Hash(string source) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n"))))[..16];

    public async Task<BuildResult> BuildAsync(string source, CancellationToken ct)
    {
        var dir = Path.Combine(cacheDirectory, Hash(source));
        var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "bot.exe" : "bot");
        if (File.Exists(exe))
        {
            return new BuildResult(true, exe, "");
        }

        await Gate.WaitAsync(ct);
        try
        {
            if (File.Exists(exe))
            {
                return new BuildResult(true, exe, "");
            }

            Directory.CreateDirectory(dir);
            var src = Path.Combine(dir, sourceFileName);
            await File.WriteAllTextAsync(src, source, ct);

            var parts = commandTemplate.Replace("{source}", src).Replace("{sortie}", exe)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var info = new ProcessStartInfo(parts[0])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = dir,
            };
            foreach (var arg in parts.Skip(1))
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info) ?? throw new InvalidOperationException($"Compilateur introuvable : {parts[0]}");
            var stderr = process.StandardError.ReadToEndAsync(ct);
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var errors = (await stderr + await stdout).Trim();
            return process.ExitCode == 0 && File.Exists(exe)
                ? new BuildResult(true, exe, errors)
                : new BuildResult(false, null, string.IsNullOrEmpty(errors) ? $"code de sortie {process.ExitCode}" : errors);
        }
        finally
        {
            Gate.Release();
        }
    }
}
