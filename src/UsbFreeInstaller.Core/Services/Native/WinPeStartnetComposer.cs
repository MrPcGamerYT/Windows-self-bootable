using System.Text;
using System.Text.RegularExpressions;

namespace UsbFreeInstaller.Core.Services.Native;

/// <summary>
/// WinPE PnP initialization must happen before drvload tries to install OEM storage
/// drivers. Keep the ISO's existing setup launcher/commands after driver loading.
/// </summary>
internal static class WinPeStartnetComposer
{
    private const string DriverLoad = "for /r \"%SystemRoot%\\SWBI-BootDrivers\" %%i in (*.inf) do drvload \"%%i\" >nul 2>&1";

    internal static string AddStorageDriverLoading(string? existing, bool includeLabConfig)
    {
        string[] lines = string.IsNullOrWhiteSpace(existing)
            ? new[] { "wpeinit" }
            : existing.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var result = new StringBuilder();
        result.AppendLine("@echo off");
        bool inserted = false;
        bool containsWpeinit = lines.Any(IsStandaloneWpeinit);
        if (!containsWpeinit)
        {
            // Custom ISO omitted wpeinit; initialize WinPE before trying to load a
            // driver or handing control back to the ISO's own setup launcher.
            result.AppendLine("wpeinit");
            AppendInjection(result, includeLabConfig);
            inserted = true;
        }
        foreach (string line in lines)
        {
            if (string.Equals(line.Trim(), "@echo off", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("SWBI-BootDrivers", StringComparison.OrdinalIgnoreCase))
                continue; // idempotent across LabConfig and storage rebuilds
            result.AppendLine(line);
            if (!inserted && IsStandaloneWpeinit(line))
            {
                AppendInjection(result, includeLabConfig);
                inserted = true;
            }
        }
        return result.ToString();
    }

    private static bool IsStandaloneWpeinit(string line) =>
        Regex.IsMatch(line.Trim(), @"^(?:call\s+)?(?:%SystemRoot%\\system32\\)?wpeinit(?:\.exe)?(?:\s+[^&|]*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void AppendInjection(StringBuilder sb, bool includeLabConfig)
    {
        if (includeLabConfig)
        {
            foreach (string key in new[] { "TPM", "SecureBoot", "RAM", "CPU", "Storage" })
                sb.AppendLine($"reg add HKLM\\SYSTEM\\Setup\\LabConfig /v Bypass{key}Check /t REG_DWORD /d 1 /f >nul 2>&1");
        }
        sb.AppendLine(DriverLoad);
    }
}
