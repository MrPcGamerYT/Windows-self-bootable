namespace UsbFreeInstaller.Core.Services.Native;

/// <summary>
/// Commits an already rebuilt and verified boot image on the installer volume without
/// overwriting the only boot.wim in place. In particular, ReplaceFile is unsupported on
/// some FAT32 volumes; File.Copy(source, boot.wim, overwrite: true) can truncate it.
/// </summary>
internal sealed class BootWimRecoveryException : IOException
{
    internal BootWimRecoveryException(string message, Exception inner) : base(message, inner) { }
}

internal static class BootWimReplacement
{
    internal static string Install(string rebuiltPath, string bootWimPath, Func<string, bool> verify,
        bool forceRenameFallback = false)
    {
        if (!File.Exists(bootWimPath))
            throw new FileNotFoundException("Original boot.wim is missing; refusing to install a rebuilt image.", bootWimPath);

        string staged = bootWimPath + ".new";
        string originalBackup = bootWimPath + ".swbi-backup";
        // Keep the original ISO image even if both LabConfig and storage driver injection
        // request a rebuild. The second pass uses a disposable rollback copy.
        bool originalAlreadyBackedUp = File.Exists(originalBackup);
        string rollback = originalAlreadyBackedUp ? bootWimPath + ".swbi-previous" : originalBackup;
        bool movedOriginal = false;

        try
        {
            File.Copy(rebuiltPath, staged, overwrite: true);
            using (var stream = new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.Flush(flushToDisk: true);

            if (!verify(staged))
                throw new InvalidDataException("Staged boot.wim did not pass WIM image verification; the original was kept.");

            if (originalAlreadyBackedUp && File.Exists(rollback)) File.Delete(rollback);
            if (!forceRenameFallback)
            {
                try
                {
                    File.Replace(staged, bootWimPath, rollback, ignoreMetadataErrors: true);
                    movedOriginal = true;
                }
                catch (IOException) when (File.Exists(staged) && File.Exists(bootWimPath))
                {
                    // FAT32 generally rejects ReplaceFile. Both files are still present;
                    // use two same-volume renames with a rollback path instead.
                }
                catch (PlatformNotSupportedException) when (File.Exists(staged) && File.Exists(bootWimPath))
                {
                    // No atomic replacement available on this filesystem.
                }
                catch (UnauthorizedAccessException) when (File.Exists(staged) && File.Exists(bootWimPath))
                {
                    // Some FAT implementations reject ReplaceFile with access denied;
                    // rename may still work. If not, preserve the existing boot.wim.
                }
            }
            if (!movedOriginal)
            {
                File.Move(bootWimPath, rollback);
                movedOriginal = true;
                File.Move(staged, bootWimPath);
            }

            if (!verify(bootWimPath))
                throw new InvalidDataException("Installed boot.wim failed WIM image verification; restoring the previous image.");

            if (originalAlreadyBackedUp && File.Exists(rollback)) File.Delete(rollback);
            return originalBackup;
        }
        catch (Exception installError)
        {
            if (movedOriginal)
            {
                if (!File.Exists(rollback))
                    throw new BootWimRecoveryException(
                        $"Boot image rollback is missing at '{rollback}'. Do not arm the installer.", installError);

                // Retain the bad image for diagnosis only if the rename succeeds; never
                // delete the last good copy before recovering the original name.
                string failed = bootWimPath + ".swbi-failed";
                try
                {
                    if (File.Exists(failed)) File.Delete(failed);
                    if (File.Exists(bootWimPath)) File.Move(bootWimPath, failed);
                    File.Move(rollback, bootWimPath);
                    if (File.Exists(failed)) File.Delete(failed);
                }
                catch (Exception restoreError)
                {
                    // Keep rollback and staged copies on disk for manual recovery. This
                    // must propagate past NativeWimApi instead of being a nonfatal warning.
                    throw new BootWimRecoveryException(
                        $"Could not restore boot.wim. The previous image is at '{rollback}'. Do not arm the installer.",
                        new AggregateException(installError, restoreError));
                }
            }
            throw;
        }
        finally
        {
            // A partially written .new must not consume the installer's remaining FAT32
            // space, but leave it in place if the boot.wim name is missing for diagnosis.
            if (File.Exists(bootWimPath) && File.Exists(staged)) File.Delete(staged);
        }
    }
}
