using System.Text;
using Microsoft.Win32;
using UsbFreeInstaller.Core.Abstractions;

namespace UsbFreeInstaller.Core.Services.Native;

public sealed record ParsedInfSummary(
    string InfFilePath,
    string ClassName,
    string ClassGuid,
    string Provider,
    bool IsMicrosoftInbox);

/// <summary>
/// Pure C# Windows DriverStore scanner and .inf parser.
/// Exports boot-critical storage controller drivers (Intel VMD/RST, AMD RAID, OEM NVMe/SATA/SCSI/VirtIO)
/// directly from %SystemRoot%\System32\DriverStore\FileRepository with ZERO reliance on pnputil.exe or powershell.exe.
/// </summary>
public static class NativeDriverStoreScanner
{
    private static readonly HashSet<string> StorageDriverClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SCSIAdapter",
        "HDC",
        "NVMeDisk",
        "SDHost"
    };

    private static readonly HashSet<string> StorageClassGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "{4d36e97b-e325-11ce-bfc1-08002be10318}", // SCSIAdapter (RAID / VMD / NVMe / VirtIO)
        "{4d36e96a-e325-11ce-bfc1-08002be10318}", // HDC (IDE / AHCI / SATA Controllers)
        "{a0a588a4-c46f-4b37-b7ea-c82fe89870c6}"  // SDHost (eMMC / SD boot controllers)
    };

    private const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    private static readonly HashSet<string> KnownInboxPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "storahci.inf",
        "stornvme.inf",
        "storufs.inf",
        "spaceport.inf",
        "vhdmp.inf",
        "volmgr.inf",
        "mshdc.inf",
        "machine.inf",
        "disk.inf",
        "volume.inf",
        "sdbus.inf"
    };

    public static int ExportBootCriticalStorageDrivers(
        string driverStoreRepositoryRoot,
        IReadOnlyList<string> destinationDirectories,
        string? activeControllerServiceName,
        ISafeLogger logger)
    {
        string? activeDriverSysName = ResolveActiveServiceSysFileName(activeControllerServiceName);
        int exportedCount = 0;

        if (!Directory.Exists(driverStoreRepositoryRoot))
        {
            // Stripped/modded Windows build deleted DriverStore\FileRepository — reconstruct from live INF + System32\drivers!
            return ReconstructLiveStorageDriversFromStrippedOs(destinationDirectories, activeDriverSysName, logger);
        }

        foreach (string packageDir in Directory.EnumerateDirectories(driverStoreRepositoryRoot))
        {
            string folderName = Path.GetFileName(packageDir);
            if (IsKnownMicrosoftInboxPackageFolder(folderName))
            {
                continue;
            }

            string[] infFiles;
            try
            {
                infFiles = Directory.GetFiles(packageDir, "*.inf", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            if (infFiles.Length == 0)
            {
                continue;
            }

            // Must include at least one .sys kernel driver binary to be a boot storage controller driver
            bool hasSysBinary;
            bool matchesActiveSys = false;
            try
            {
                string[] sysFiles = Directory.GetFiles(packageDir, "*.sys", SearchOption.AllDirectories);
                hasSysBinary = sysFiles.Length > 0;
                if (!string.IsNullOrWhiteSpace(activeDriverSysName))
                {
                    matchesActiveSys = sysFiles.Any(s =>
                        string.Equals(Path.GetFileName(s), activeDriverSysName, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch
            {
                continue;
            }

            if (!hasSysBinary)
            {
                continue;
            }

            bool shouldExport = matchesActiveSys;
            if (!shouldExport)
            {
                foreach (string infPath in infFiles)
                {
                    ParsedInfSummary? summary = TryParseInfVersionSection(infPath);
                    if (summary is null)
                    {
                        continue;
                    }

                    bool isStorageClass =
                        StorageDriverClasses.Contains(summary.ClassName) ||
                        StorageClassGuids.Contains(summary.ClassGuid);

                    if (isStorageClass && !summary.IsMicrosoftInbox)
                    {
                        shouldExport = true;
                        break;
                    }
                }
            }

            if (!shouldExport)
            {
                continue;
            }

            try
            {
                foreach (string destRoot in destinationDirectories)
                {
                    string targetPkgDir = Path.Combine(destRoot, folderName);
                    CopyDirectoryAll(packageDir, targetPkgDir);
                }

                exportedCount++;
                logger.Info("NativeDrivers", $"Exported storage driver package '{folderName}' directly from DriverStore.");
            }
            catch (Exception ex)
            {
                logger.Warn("NativeDrivers", $"Skipped driver package '{folderName}': {ex.Message}");
            }
        }

        if (exportedCount == 0)
        {
            exportedCount += ReconstructLiveStorageDriversFromStrippedOs(destinationDirectories, activeDriverSysName, logger);
        }

        return exportedCount;
    }

    private static int ReconstructLiveStorageDriversFromStrippedOs(
        IReadOnlyList<string> destinationDirectories,
        string? activeDriverSysName,
        ISafeLogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        try
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string infDir = Path.Combine(winDir, "INF");
            string driversDir = Path.Combine(winDir, "System32", "drivers");
            string catRootDir = Path.Combine(winDir, "System32", "CatRoot", "{F750E6C3-38EE-11D1-85E5-00C04FC295EE}");

            if (!Directory.Exists(infDir) || !Directory.Exists(driversDir))
            {
                return 0;
            }

            int reconstructed = 0;
            foreach (string oemInf in Directory.EnumerateFiles(infDir, "oem*.inf", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string infText = ReadInfTextAutoEncoding(oemInf);
                    ParsedInfSummary summary = ParseInfVersionText(infText, oemInf);

                    bool isOemStorage = !summary.IsMicrosoftInbox &&
                        (StorageDriverClasses.Contains(summary.ClassName) ||
                         StorageClassGuids.Contains(summary.ClassGuid));

                    if (!isOemStorage &&
                        (string.IsNullOrWhiteSpace(activeDriverSysName) ||
                         !infText.Contains(activeDriverSysName, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // Extract referenced .sys files that exist in live System32\drivers
                    var foundSys = new List<string>();
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                        infText,
                        @"([A-Za-z0-9_\-\.]+\.sys)\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        string sysName = m.Groups[1].Value;
                        string candidateSys = Path.Combine(driversDir, sysName);
                        if (File.Exists(candidateSys) && !foundSys.Contains(candidateSys, StringComparer.OrdinalIgnoreCase))
                        {
                            foundSys.Add(candidateSys);
                        }
                    }

                    if (foundSys.Count == 0)
                    {
                        continue;
                    }

                    string pkgFolderName = $"reconstructed_{Path.GetFileNameWithoutExtension(oemInf)}";
                    string catCandidate = Path.Combine(catRootDir, Path.ChangeExtension(Path.GetFileName(oemInf), ".cat"));

                    foreach (string destRoot in destinationDirectories)
                    {
                        string pkgDest = Path.Combine(destRoot, pkgFolderName);
                        Directory.CreateDirectory(pkgDest);
                        File.Copy(oemInf, Path.Combine(pkgDest, Path.GetFileName(oemInf)), overwrite: true);
                        if (File.Exists(catCandidate))
                        {
                            File.Copy(catCandidate, Path.Combine(pkgDest, Path.GetFileName(catCandidate)), overwrite: true);
                        }
                        foreach (string sysFile in foundSys)
                        {
                            File.Copy(sysFile, Path.Combine(pkgDest, Path.GetFileName(sysFile)), overwrite: true);
                        }
                    }

                    reconstructed++;
                    logger.Info("NativeDrivers", $"Reconstructed live OEM storage driver '{pkgFolderName}' from %SystemRoot%\\INF + System32\\drivers (stripped DriverStore self-heal).");
                }
                catch
                {
                    // Skip unreadable OEM inf
                }
            }

            return reconstructed;
        }
        catch
        {
            return 0;
        }
    }

    public static int ExportOemNetworkDrivers(
        string driverStoreRepositoryRoot,
        IReadOnlyList<string> destinationDirectories,
        ISafeLogger logger)
    {
        if (!Directory.Exists(driverStoreRepositoryRoot))
        {
            return 0;
        }

        int exportedCount = 0;
        foreach (string packageDir in Directory.EnumerateDirectories(driverStoreRepositoryRoot))
        {
            string folderName = Path.GetFileName(packageDir);
            if (folderName.StartsWith("net", StringComparison.OrdinalIgnoreCase) &&
                (folderName.StartsWith("netvwifibus.inf_", StringComparison.OrdinalIgnoreCase) ||
                 folderName.StartsWith("netrndis.inf_", StringComparison.OrdinalIgnoreCase) ||
                 folderName.StartsWith("netavpna.inf_", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string[] infFiles;
            try
            {
                infFiles = Directory.GetFiles(packageDir, "*.inf", SearchOption.TopDirectoryOnly);
                if (infFiles.Length == 0 || Directory.GetFiles(packageDir, "*.sys", SearchOption.AllDirectories).Length == 0)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            bool isOemNet = false;
            foreach (string infPath in infFiles)
            {
                ParsedInfSummary? summary = TryParseInfVersionSection(infPath);
                if (summary is not null &&
                    !summary.IsMicrosoftInbox &&
                    (summary.ClassName.Equals("Net", StringComparison.OrdinalIgnoreCase) ||
                     summary.ClassGuid.Equals(NetClassGuid, StringComparison.OrdinalIgnoreCase)))
                {
                    isOemNet = true;
                    break;
                }
            }

            if (!isOemNet) continue;

            try
            {
                foreach (string destRoot in destinationDirectories)
                {
                    CopyDirectoryAll(packageDir, Path.Combine(destRoot, folderName));
                }
                exportedCount++;
                logger.Info("NativeDrivers", $"Exported OEM network driver package '{folderName}' directly from DriverStore.");
            }
            catch (Exception ex)
            {
                logger.Warn("NativeDrivers", $"Skipped network driver '{folderName}': {ex.Message}");
            }
        }

        return exportedCount;
    }

    public static ParsedInfSummary? TryParseInfVersionSection(string infFilePath)
    {
        try
        {
            string text = ReadInfTextAutoEncoding(infFilePath);
            return ParseInfVersionText(text, infFilePath);
        }
        catch
        {
            return null;
        }
    }

    public static ParsedInfSummary ParseInfVersionText(string infContent, string infFilePath = "")
    {
        bool inVersion = false;
        bool inStrings = false;

        string rawClass = string.Empty;
        string rawClassGuid = string.Empty;
        string rawProvider = string.Empty;
        var stringsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(infContent);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            int semi = line.IndexOf(';');
            if (semi >= 0)
            {
                line = line[..semi];
            }

            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                string sec = trimmed[1..^1].Trim();
                inVersion = sec.Equals("Version", StringComparison.OrdinalIgnoreCase);
                inStrings = sec.StartsWith("Strings", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            int eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = trimmed[..eq].Trim();
            string val = trimmed[(eq + 1)..].Trim().Trim('"');

            if (inVersion)
            {
                if (key.Equals("Class", StringComparison.OrdinalIgnoreCase))
                {
                    rawClass = val;
                }
                else if (key.Equals("ClassGUID", StringComparison.OrdinalIgnoreCase))
                {
                    rawClassGuid = val;
                }
                else if (key.Equals("Provider", StringComparison.OrdinalIgnoreCase))
                {
                    rawProvider = val;
                }
            }
            else if (inStrings)
            {
                stringsMap.TryAdd(key, val);
            }
        }

        string resolvedProvider = ResolveInfToken(rawProvider, stringsMap);
        string resolvedClass = ResolveInfToken(rawClass, stringsMap);
        string resolvedGuid = ResolveInfToken(rawClassGuid, stringsMap);

        bool isMsftInbox =
            rawProvider.Equals("%Msft%", StringComparison.OrdinalIgnoreCase) ||
            resolvedProvider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
            resolvedProvider.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase);

        // If an OEM driver sits under FileRepository with a non-inbox filename (e.g. iaStorVD.inf_amd64_...),
        // its Provider is Intel/AMD/etc., so isMsftInbox is false.
        return new ParsedInfSummary(infFilePath, resolvedClass, resolvedGuid, resolvedProvider, isMsftInbox);
    }

    private static string ResolveInfToken(string value, Dictionary<string, string> stringsMap)
    {
        if (value.Length >= 3 && value.StartsWith('%') && value.EndsWith('%'))
        {
            string token = value[1..^1];
            if (stringsMap.TryGetValue(token, out string? resolved))
            {
                return resolved;
            }
        }
        return value;
    }

    private static bool IsKnownMicrosoftInboxPackageFolder(string folderName)
    {
        foreach (string prefix in KnownInboxPrefixes)
        {
            if (folderName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string ReadInfTextAutoEncoding(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string? ResolveActiveServiceSysFileName(string? serviceName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(serviceName))
        {
            return null;
        }

        try
        {
            using RegistryKey? svcKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            string? imagePath = svcKey?.GetValue("ImagePath") as string;
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                return Path.GetFileName(imagePath.Replace('/', '\\'));
            }
        }
        catch
        {
            // Ignore registry lookup errors
        }

        return $"{serviceName}.sys";
    }

    private static void CopyDirectoryAll(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        foreach (string file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcDir, file);
            string dst = Path.Combine(dstDir, rel);
            string? parent = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            File.Copy(file, dst, overwrite: true);
        }
    }
}
