using System.Threading;

namespace UsbFreeInstaller.App.Infrastructure;

/// <summary>
/// Machine-wide guard that stops two SWBI windows from preparing or cleaning disks at the same time.
/// Two concurrent instances can otherwise race each other: one deploys payload to the installer partition
/// while the other finds it (manifest already written), considers it owned, and deletes it.
/// </summary>
internal static class SingleInstanceGuard
{
    private const string MutexName = @"SWBI-SelfWindowsBootableInstaller-SingleInstance";

    // Kept alive for the process lifetime: a collected Mutex releases its handle (and ownership) early,
    // which would let a second instance through while this one is still running.
    private static Mutex? _mutex;

    internal static bool TryAcquire(out string reason)
    {
        reason = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        foreach (string scope in new[] { @"Global\", @"Local\" })
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(initiallyOwned: false, name: scope + MutexName);
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner crashed while holding it; ownership transfers to us.
                    acquired = true;
                }

                if (!acquired)
                {
                    mutex.Dispose();
                    reason = "Another SWBI window is already running on this PC.";
                    return false;
                }

                _mutex = mutex;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // A mutex in this scope belongs to another user/session; fall through to the next scope.
                mutex?.Dispose();
            }
            catch (Exception)
            {
                mutex?.Dispose();
                // Never block startup because of a mutex problem — the disk-level identity checks still apply.
                return true;
            }
        }

        return true;
    }
}
