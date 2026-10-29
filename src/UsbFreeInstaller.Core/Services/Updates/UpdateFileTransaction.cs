using System.Security.Cryptography;

namespace UsbFreeInstaller.Core.Services.Updates;

/// <summary>Rechecks both files before replacing; retains a backup and attempts rollback if restart cannot start.</summary>
public static class UpdateFileTransaction
{
    public static async Task<string> HashFileAsync(string path, CancellationToken token = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    public static async Task ApplyAsync(UpdateApplyPlan plan, Func<string, bool> restart, CancellationToken token = default)
    {
        ValidatePaths(plan);
        if (StandaloneLauncher.Read(plan.TargetPath).Flavor != plan.Flavor || StandaloneLauncher.Read(plan.StagedPath).Flavor != plan.Flavor ||
            !string.Equals(await HashFileAsync(plan.TargetPath, token).ConfigureAwait(false), plan.OldSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(await HashFileAsync(plan.StagedPath, token).ConfigureAwait(false), plan.NewSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The original or downloaded launcher changed after verification; no replacement was attempted.");
        token.ThrowIfCancellationRequested();
        // A native launcher started with --wait can outlive its child for a few moments.
        // Retry only sharing-related I/O failures; never fall back to overwriting in place.
        for (int attempt = 0; ; attempt++)
        {
            try { File.Replace(plan.StagedPath, plan.TargetPath, plan.BackupPath); break; }
            catch (IOException) when (attempt < 15)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                if (!string.Equals(await HashFileAsync(plan.TargetPath, token).ConfigureAwait(false), plan.OldSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(await HashFileAsync(plan.StagedPath, token).ConfigureAwait(false), plan.NewSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The launcher changed while waiting for its file lock; no replacement was attempted.");
            }
        }
        try
        {
            if (!string.Equals(await HashFileAsync(plan.BackupPath, token).ConfigureAwait(false), plan.OldSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The retained backup does not match the original launcher.");
            // Keep the verified target open without write/delete sharing through CreateProcess,
            // so another process cannot swap it between the hash check and restart.
            using var installed = new FileStream(plan.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
            string installedHash = Convert.ToHexString(await SHA256.HashDataAsync(installed, token).ConfigureAwait(false));
            if (!string.Equals(installedHash, plan.NewSha256, StringComparison.OrdinalIgnoreCase) || !restart(plan.TargetPath))
                throw new InvalidOperationException("The updated launcher could not be verified or started.");
        }
        catch (Exception error)
        {
            string failedCopy = plan.StagedPath;
            try
            {
                File.Replace(plan.BackupPath, plan.TargetPath, failedCopy);
            }
            catch (Exception rollback)
            {
                throw new InvalidOperationException($"Update failed and the original could not be restored automatically. Backup: {plan.BackupPath}. {rollback.Message}", error);
            }
            throw new InvalidOperationException("Update failed; the original executable was restored. The downloaded copy was retained for inspection.", error);
        }
    }

    private static void ValidatePaths(UpdateApplyPlan plan)
    {
        string target = Path.GetFullPath(plan.TargetPath);
        string staged = Path.GetFullPath(plan.StagedPath);
        string backup = Path.GetFullPath(plan.BackupPath);
        string? directory = Path.GetDirectoryName(target);
        if (!Path.IsPathFullyQualified(plan.TargetPath) || !Path.IsPathFullyQualified(plan.StagedPath) || !Path.IsPathFullyQualified(plan.BackupPath) ||
            !string.Equals(Path.GetDirectoryName(staged), directory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(backup), directory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(staged).StartsWith(".swbi-update-", StringComparison.Ordinal) ||
            !Path.GetFileName(backup).StartsWith(".swbi-backup-", StringComparison.Ordinal) ||
            string.Equals(target, staged, StringComparison.OrdinalIgnoreCase) || string.Equals(target, backup, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(backup) || plan.OldSha256.Length != 64 || plan.NewSha256.Length != 64 ||
            !plan.OldSha256.All(Uri.IsHexDigit) || !plan.NewSha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The update paths or verification hashes are invalid.");
    }
}
