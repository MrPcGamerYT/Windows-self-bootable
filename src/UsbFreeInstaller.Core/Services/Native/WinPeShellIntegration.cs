using System.Text;
using System.Text.RegularExpressions;
using UsbFreeInstaller.Core.Abstractions;

namespace UsbFreeInstaller.Core.Services.Native;

/// <summary>
/// Setup boot.wim often launches X:\setup.exe (or Winpeshl.ini apps) instead of
/// running startnet.cmd. Register a short, synchronous driver-loader application
/// before that shell app; otherwise a perfectly good INF inside boot.wim may never
/// be loaded before Setup enumerates disks. Unsupported shell configurations fail
/// closed: the caller keeps the ISO's original boot.wim and stages F6 drivers.
/// </summary>
internal static class WinPeShellIntegration
{
    internal const string LoaderCommand = @"%SYSTEMROOT%\System32\cmd.exe, /c ""%SYSTEMROOT%\System32\swbi-load-drivers.cmd""";

    internal static void EnsureDriverLoaderRunsBeforeSetup(string imageRoot, bool includeLabConfig, ISafeLogger logger)
    {
        string system32 = Path.Combine(imageRoot, "Windows", "System32");
        string shellIni = Path.Combine(system32, "winpeshl.ini");
        string setup = Path.Combine(imageRoot, "setup.exe");
        string? updatedIni = null;

        if (File.Exists(shellIni))
        {
            string existing = File.ReadAllText(shellIni);
            // Do not corrupt custom INI files that use an unknown encoding or shell
            // directive. A failed injection is safer than silently skipping Setup.
            if (existing.Any(c => c > 127) || !TryPrependLoader(existing, out updatedIni))
                throw new InvalidDataException("Unsupported WinPE winpeshl.ini; cannot guarantee storage drivers load before Setup.");
        }
        else if (File.Exists(setup))
        {
            // Standard Windows Setup image 2 commonly has X:\setup.exe but no INI.
            // Winpeshl launches setup directly and never executes startnet.cmd.
            updatedIni = $"[LaunchApps]\r\n{LoaderCommand}\r\n%SYSTEMDRIVE%\\setup.exe\r\n";
        }

        if (updatedIni is null)
        {
            logger.Info("NativeWim", "No alternate WinPE shell found; startnet.cmd will initialize WinPE and load storage drivers.");
            return;
        }

        // Winpeshl waits for each LaunchApps entry to exit before starting the next.
        // Use cmd.exe to execute a batch script rather than placing batch commands in INI.
        string loader = Path.Combine(system32, "swbi-load-drivers.cmd");
        File.WriteAllText(loader, WinPeStartnetComposer.AddStorageDriverLoading("wpeinit", includeLabConfig), Encoding.ASCII);
        File.WriteAllText(shellIni, updatedIni, Encoding.ASCII);
        logger.Info("NativeWim", "Registered WinPE storage-driver loader before the Setup shell in winpeshl.ini.");
    }

    internal static bool TryPrependLoader(string existing, out string? updated)
    {
        updated = null;
        var headers = Regex.Matches(existing, @"(?m)^[ \t]*\[(LaunchApp|LaunchApps)\][ \t]*\r?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (headers.Count == 0) return false;
        if (existing.Contains("swbi-load-drivers.cmd", StringComparison.OrdinalIgnoreCase))
        {
            updated = existing; // already registered during a previous rebuild
            return true;
        }

        if (string.Equals(headers[0].Groups[1].Value, "LaunchApps", StringComparison.OrdinalIgnoreCase))
        {
            int insertAt = headers[0].Index + headers[0].Length;
            if (insertAt > 0 && existing[insertAt - 1] == '\r') insertAt--;
            string newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            updated = existing.Insert(insertAt, newline + LoaderCommand);
            return true;
        }
        if (headers.Count == 1 && string.Equals(headers[0].Groups[1].Value, "LaunchApp", StringComparison.OrdinalIgnoreCase))
        {
            updated = $"[LaunchApps]\r\n{LoaderCommand}\r\n\r\n" + existing;
            return true;
        }
        // An ambiguous mixture of both sections may change ordering; do not risk
        // replacing an existing custom shell or launching Setup before drivers.
        return false;
    }
}
