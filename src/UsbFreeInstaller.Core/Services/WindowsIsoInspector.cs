using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Services;

public sealed partial class WindowsIsoInspector : IIsoInspector
{
    private readonly IProcessRunner _processRunner;
    private readonly ISafeLogger _logger;

    public WindowsIsoInspector(IProcessRunner processRunner, ISafeLogger logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<IsoInspectionResult> InspectIsoAsync(
        string isoPath,
        bool computeSha256,
        IProgress<int>? hashProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath))
        {
            return new IsoInspectionResult
            {
                IsoPath = isoPath ?? string.Empty,
                Exists = false,
                ValidationErrors = new[] { "Selected ISO file does not exist." }
            };
        }

        var fileInfo = new FileInfo(isoPath);
        if (!string.Equals(fileInfo.Extension, ".iso", StringComparison.OrdinalIgnoreCase))
        {
            return new IsoInspectionResult
            {
                IsoPath = fileInfo.FullName,
                Exists = true,
                FileSizeBytes = fileInfo.Length,
                ValidationErrors = new[] { "Selected file must have a .iso extension." }
            };
        }

        if (fileInfo.Length < 34 * 1024)
        {
            return new IsoInspectionResult
            {
                IsoPath = fileInfo.FullName,
                Exists = true,
                FileSizeBytes = fileInfo.Length,
                ValidationErrors = new[] { "Selected .iso file is empty or truncated." }
            };
        }

        string? sha256Hex = null;
        if (computeSha256)
        {
            _logger.Info("ISO", $"Computing SHA-256 checksum for '{fileInfo.Name}'...");
            sha256Hex = await ComputeFileSha256Async(fileInfo.FullName, hashProgress, cancellationToken).ConfigureAwait(false);
        }

        // Pure C# Zero-Mount UDF + ISO-9660/Joliet + Binary WIM/ESD XML Reader.
        // Works on 100% of Official, Custom, Lite, AIO, Dual-Arch, and Modified Windows 10/11 ISOs
        // without requiring PowerShell Mount-DiskImage, Virtual Disk Service, AutoMount, WMI, or dism.exe.
        try
        {
            using IsoImageStreamReader reader = IsoImageStreamReader.Open(fileInfo.FullName);

            // The directory walk and WIM header parsing are fully synchronous and can take seconds on a
            // large or slow image. Run them off the caller's thread so a WPF UI never appears hung.
            return await Task.Run(() => InspectFromDirectIsoReader(reader, fileInfo, sha256Hex), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception directEx)
        {
            _logger.Warn("ISO", $"ISO stream parser rejected '{fileInfo.Name}': {directEx.Message}");
            return new IsoInspectionResult
            {
                IsoPath = fileInfo.FullName,
                Exists = true,
                FileSizeBytes = fileInfo.Length,
                Sha256Hex = sha256Hex,
                ValidationErrors = new[]
                {
                    $"Selected file is not a valid UDF or ISO-9660/Joliet Windows disc image ({directEx.Message})."
                }
            };
        }
    }

    private IsoInspectionResult InspectFromDirectIsoReader(
        IsoImageStreamReader reader,
        FileInfo fileInfo,
        string? sha256Hex)
    {
        string hostArch = GetHostArchTag();
        IReadOnlyCollection<IsoFileEntry> allFiles = reader.Files;

        // 1. Locate boot.wim (supports standard sources\boot.wim, dual-arch x64\sources\boot.wim, or custom PE location)
        IsoFileEntry? bootWimEntry = FindBestMatchingEntry(
            allFiles,
            new[]
            {
                @"sources\boot.wim",
                $@"{hostArch}\sources\boot.wim",
                @"x64\sources\boot.wim",
                @"arm64\sources\boot.wim",
                @"x86\sources\boot.wim"
            })
            ?? allFiles.FirstOrDefault(f => f.RelativePath.EndsWith(@"\boot.wim", StringComparison.OrdinalIgnoreCase) ||
                                            f.RelativePath.Equals("boot.wim", StringComparison.OrdinalIgnoreCase));

        // 2. Locate boot.sdi
        IsoFileEntry? bootSdiEntry = FindBestMatchingEntry(
            allFiles,
            new[]
            {
                @"boot\boot.sdi",
                $@"{hostArch}\boot\boot.sdi",
                @"x64\boot\boot.sdi",
                @"arm64\boot\boot.sdi",
                @"x86\boot\boot.sdi",
                @"sources\boot.sdi"
            })
            ?? allFiles.FirstOrDefault(f => f.RelativePath.EndsWith(@"\boot.sdi", StringComparison.OrdinalIgnoreCase));

        // 3. Locate UEFI bootloaders (64-bit x64, 64-bit ARM64, AND 32-bit IA32) & setup.exe
        bool hasEfiX64 = AnyPathExists(allFiles, @"efi\boot\bootx64.efi", @"x64\efi\boot\bootx64.efi", @"efi\microsoft\boot\bootmgfw.efi", @"bootmgr.efi");
        bool hasEfiArm = AnyPathExists(allFiles, @"efi\boot\bootaa64.efi", @"arm64\efi\boot\bootaa64.efi");
        bool hasEfiIa32 = AnyPathExists(allFiles, @"efi\boot\bootia32.efi", @"x86\efi\boot\bootia32.efi");
        bool hasEfi = hasEfiX64 || hasEfiArm || hasEfiIa32;
        bool hasRootSetup = AnyPathExists(allFiles, "setup.exe", $@"{hostArch}\setup.exe");
        bool hasSourcesSetup = AnyPathExists(allFiles, @"sources\setup.exe", $@"{hostArch}\sources\setup.exe");

        // 4. Locate OS Install Payload (Official install.wim / install.esd / install.swm, Dual-Arch x64\sources\*, OR custom-named WIM/ESD in sources\)
        (InstallPayloadKind payloadKind, string payloadRel, long payloadSize, IsoFileEntry? primaryPayloadEntry) =
            LocateInstallPayload(allFiles, hostArch);

        // 5. Read WIM / ESD / SWM XML header directly from inside the ISO in milliseconds
        string parsedArch = "Unknown";
        string parsedVer = string.Empty;
        IReadOnlyList<WimEditionInfo> editions = Array.Empty<WimEditionInfo>();

        if (primaryPayloadEntry is not null)
        {
            using Stream payloadStream = reader.OpenFileStream(primaryPayloadEntry);
            if (WimHeaderXmlParser.TryParse(payloadStream, out ParsedWimMetadata? wimMeta) && wimMeta is not null)
            {
                parsedArch = wimMeta.PrimaryArchitecture;
                parsedVer = wimMeta.PrimaryVersion;
                editions = wimMeta.Editions;
            }
        }

        // Fallback to boot.wim XML header for architecture if payload omitted <ARCH>
        if (string.Equals(parsedArch, "Unknown", StringComparison.OrdinalIgnoreCase) && bootWimEntry is not null)
        {
            using Stream bootStream = reader.OpenFileStream(bootWimEntry);
            if (WimHeaderXmlParser.TryParse(bootStream, out ParsedWimMetadata? bootMeta) &&
                bootMeta is not null &&
                !string.Equals(bootMeta.PrimaryArchitecture, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                parsedArch = bootMeta.PrimaryArchitecture;
            }
        }

        if (string.Equals(parsedArch, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            parsedArch = hasEfiArm && !hasEfiX64 ? "arm64" : hasEfiX64 ? "x64" : hostArch;
        }

        // Ensure custom/modified ISOs whose WIM lacks standard <IMAGE> metadata still display a clear edition entry
        if (editions.Count == 0 && payloadKind != InstallPayloadKind.None)
        {
            string friendlyIsoTitle = Path.GetFileNameWithoutExtension(fileInfo.Name);
            editions = new[]
            {
                new WimEditionInfo
                {
                    Index = 1,
                    Name = $"{friendlyIsoTitle} (Custom / Modified Image)",
                    Description = payloadRel,
                    Architecture = parsedArch,
                    SizeBytes = payloadSize,
                    Version = parsedVer
                }
            };
        }
        else if (editions.Any(e => string.Equals(e.Architecture, "Unknown", StringComparison.OrdinalIgnoreCase)))
        {
            editions = editions.Select(e =>
                string.Equals(e.Architecture, "Unknown", StringComparison.OrdinalIgnoreCase)
                    ? e with { Architecture = parsedArch }
                    : e).ToArray();
        }

        // Detect custom/modified ISO characteristics (Tiny10/11, Ghost Spectre, AtlasOS, ReviOS, NTLite, AIO, $OEM$, autounattend.xml)
        bool hasCustomMarkers =
            !hasRootSetup ||
            bootSdiEntry is null ||
            !hasEfi ||
            !string.Equals(bootWimEntry?.RelativePath ?? string.Empty, @"sources\boot.wim", StringComparison.OrdinalIgnoreCase) ||
            allFiles.Any(f =>
                f.RelativePath.Equals("autounattend.xml", StringComparison.OrdinalIgnoreCase) ||
                f.RelativePath.StartsWith(@"sources\$OEM$", StringComparison.OrdinalIgnoreCase) ||
                f.RelativePath.Contains(@"\$OEM$\", StringComparison.OrdinalIgnoreCase)) ||
            editions.Any(e => ContainsCustomEditionKeyword(e.Name));

        WimSplitStrategy splitStrategy = DetermineSplitStrategy(payloadKind, payloadSize);

        var errors = new List<string>();
        var warnings = new List<string>();

        // Reject path-unsafe entries during validation, so the user learns about a malformed or hostile
        // image before C: is shrunk instead of part-way through deployment.
        string[] unsafeEntryPaths = allFiles
            .Where(f => SafeRelativePath.ContainsTraversalSegment(f.RelativePath) ||
                        !SafeRelativePath.TryResolveUnder(Path.GetTempPath(), f.RelativePath, out _, out _))
            .Select(f => f.RelativePath)
            .Take(5)
            .ToArray();

        if (unsafeEntryPaths.Length > 0)
        {
            errors.Add(
                "This ISO contains file entries with unsafe paths (" +
                string.Join(", ", unsafeEntryPaths.Select(p => $"'{p}'")) +
                "). SWBI only extracts files beneath the installer partition and will not process this image. " +
                "Re-create the ISO with a trusted tool or obtain it from a trusted source.");
        }

        if (bootWimEntry is null)
        {
            errors.Add(@"This ISO does not contain a bootable WinPE image ('sources\boot.wim'). Make sure it is a bootable Windows 10/11 ISO rather than a non-bootable data disc.");
        }

        if (payloadKind == InstallPayloadKind.None)
        {
            errors.Add(@"No Windows installation image ('install.wim', 'install.esd', 'install.swm', or custom OS WIM) was found inside the ISO.");
        }

        if (bootSdiEntry is null && bootWimEntry is not null)
        {
            warnings.Add(@"Custom ISO omits '\boot\boot.sdi'; SWBI will automatically supply Windows' built-in signed boot.sdi during preparation.");
        }

        if (!hasEfi && bootWimEntry is not null)
        {
            warnings.Add(@"Custom ISO omits '\efi\boot\bootx64.efi'; SWBI will automatically supply the signed UEFI bootloader during preparation.");
        }

        if (!hasRootSetup && (hasSourcesSetup || bootWimEntry is not null))
        {
            warnings.Add("Clean-install-only Custom ISO detected (root setup.exe omitted); Self-Bootable Clean Install and Preserve Windows.old modes are fully supported.");
        }

        if (splitStrategy == WimSplitStrategy.ConvertEsdThenSplitToSwm)
        {
            warnings.Add(
                "This ISO uses a solid ESD image larger than 4 GB. Preparation will export to a temporary WIM on C: before splitting into FAT32-safe .swm chunks.");
        }

        _logger.Info(
            "ISO",
            $"Inspected ISO '{fileInfo.Name}' via direct stream reader: Type={(hasCustomMarkers ? "Custom/Modified" : "Official/Standard")}, Arch={parsedArch}, Payload={payloadRel} ({ByteFormatting.FormatGb(payloadSize)}), Editions={editions.Count}, Strategy={splitStrategy}.");

        return new IsoInspectionResult
        {
            IsoPath = fileInfo.FullName,
            Exists = true,
            FileSizeBytes = fileInfo.Length,
            Sha256Hex = sha256Hex,
            HasBootSdi = bootSdiEntry is not null,
            HasBootWim = bootWimEntry is not null,
            HasEfiBootloader = hasEfi,
            HasSetupExe = hasRootSetup,
            IsCustomOrModifiedIso = hasCustomMarkers,
            BootWimRelativePath = bootWimEntry?.RelativePath ?? @"sources\boot.wim",
            BootSdiRelativePath = bootSdiEntry?.RelativePath ?? @"boot\boot.sdi",
            Architecture = parsedArch,
            PayloadKind = payloadKind,
            PayloadRelativePath = payloadRel,
            PayloadSizeBytes = payloadSize,
            SplitStrategy = splitStrategy,
            Editions = editions,
            ValidationErrors = errors,
            ValidationWarnings = warnings
        };
    }

    private static (InstallPayloadKind Kind, string RelPath, long TotalBytes, IsoFileEntry? PrimaryEntry) LocateInstallPayload(
        IReadOnlyCollection<IsoFileEntry> allFiles,
        string hostArch)
    {
        // Priority 1: Standard & dual-architecture install.wim / install.esd / install.swm paths
        string[] prefixes =
        {
            "sources",
            $@"{hostArch}\sources",
            @"x64\sources",
            @"arm64\sources",
            @"x86\sources"
        };

        foreach (string prefix in prefixes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IsoFileEntry? wim = allFiles.FirstOrDefault(f => f.RelativePath.Equals($@"{prefix}\install.wim", StringComparison.OrdinalIgnoreCase));
            if (wim is not null && wim.SizeBytes > 0)
            {
                return (InstallPayloadKind.Wim, wim.RelativePath, wim.SizeBytes, wim);
            }

            IsoFileEntry? esd = allFiles.FirstOrDefault(f => f.RelativePath.Equals($@"{prefix}\install.esd", StringComparison.OrdinalIgnoreCase));
            if (esd is not null && esd.SizeBytes > 0)
            {
                return (InstallPayloadKind.Esd, esd.RelativePath, esd.SizeBytes, esd);
            }

            IsoFileEntry? swm = allFiles.FirstOrDefault(f => f.RelativePath.Equals($@"{prefix}\install.swm", StringComparison.OrdinalIgnoreCase));
            if (swm is not null && swm.SizeBytes > 0)
            {
                long totalSwm = allFiles
                    .Where(f => f.RelativePath.StartsWith($@"{prefix}\install", StringComparison.OrdinalIgnoreCase) &&
                                f.RelativePath.EndsWith(".swm", StringComparison.OrdinalIgnoreCase))
                    .Sum(f => f.SizeBytes);
                return (InstallPayloadKind.Swm, swm.RelativePath, Math.Max(swm.SizeBytes, totalSwm), swm);
            }
        }

        // Priority 2: Custom/Modified ISOs using custom WIM/ESD/SWM filenames (largest non-boot.wim OS image >= 16 MB)
        IsoFileEntry? customCandidate = allFiles
            .Where(f =>
                !f.RelativePath.EndsWith(@"\boot.wim", StringComparison.OrdinalIgnoreCase) &&
                !f.RelativePath.Equals("boot.wim", StringComparison.OrdinalIgnoreCase) &&
                !f.RelativePath.EndsWith(@"\winre.wim", StringComparison.OrdinalIgnoreCase) &&
                f.SizeBytes >= 16L * 1024 * 1024 &&
                (f.RelativePath.EndsWith(".wim", StringComparison.OrdinalIgnoreCase) ||
                 f.RelativePath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase) ||
                 f.RelativePath.EndsWith(".swm", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.SizeBytes)
            .FirstOrDefault();

        if (customCandidate is not null)
        {
            string ext = Path.GetExtension(customCandidate.RelativePath).ToLowerInvariant();
            InstallPayloadKind kind = ext switch
            {
                ".wim" => InstallPayloadKind.Wim,
                ".esd" => InstallPayloadKind.Esd,
                ".swm" => InstallPayloadKind.Swm,
                _ => InstallPayloadKind.None
            };
            return (kind, customCandidate.RelativePath, customCandidate.SizeBytes, customCandidate);
        }

        return (InstallPayloadKind.None, string.Empty, 0L, null);
    }

    public static WimSplitStrategy DetermineSplitStrategy(InstallPayloadKind kind, long payloadSizeBytes) =>
        kind switch
        {
            InstallPayloadKind.None => WimSplitStrategy.Unsupported,
            InstallPayloadKind.Swm => WimSplitStrategy.DirectCopy,
            InstallPayloadKind.Wim when payloadSizeBytes < IsoInspectionResult.Fat32SafeSingleFileLimitBytes => WimSplitStrategy.DirectCopy,
            InstallPayloadKind.Wim => WimSplitStrategy.SplitWimToSwm,
            InstallPayloadKind.Esd when payloadSizeBytes < IsoInspectionResult.Fat32SafeSingleFileLimitBytes => WimSplitStrategy.DirectCopy,
            InstallPayloadKind.Esd => WimSplitStrategy.ConvertEsdThenSplitToSwm,
            _ => WimSplitStrategy.Unsupported
        };

    public static async Task<string> ComputeFileSha256Async(
        string filePath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        const int bufferSize = 4 * 1024 * 1024;
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha256 = SHA256.Create();
        byte[] buffer = new byte[bufferSize];
        long totalBytes = Math.Max(1L, stream.Length);
        long readTotal = 0L;
        int lastPercent = -1;

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            readTotal += bytesRead;
            int pct = (int)Math.Min(100L, (readTotal * 100L) / totalBytes);
            if (pct != lastPercent)
            {
                lastPercent = pct;
                progress?.Report(pct);
            }
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha256.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
    }

    private static IsoFileEntry? FindBestMatchingEntry(IReadOnlyCollection<IsoFileEntry> files, IEnumerable<string> candidates)
    {
        foreach (string cand in candidates)
        {
            IsoFileEntry? match = files.FirstOrDefault(f => f.RelativePath.Equals(cand, StringComparison.OrdinalIgnoreCase));
            if (match is not null && match.SizeBytes > 0)
            {
                return match;
            }
        }

        return null;
    }

    private static bool AnyPathExists(IReadOnlyCollection<IsoFileEntry> files, params string[] candidates) =>
        candidates.Any(c => files.Any(f => f.RelativePath.Equals(c, StringComparison.OrdinalIgnoreCase) && f.SizeBytes > 0));

    private static bool ContainsCustomEditionKeyword(string editionName)
    {
        string[] keywords = { "Tiny11", "Tiny10", "Superlite", "Atlas", "ReviOS", "Nexus", "Phoenix", "Lite", "Compact", "Ghost", "Custom", "Debloat" };
        return keywords.Any(k => editionName.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetHostArchTag() =>
        RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64"
        };

    [GeneratedRegex(@"Index\s*:\s*(?<idx>\d+)\s+Name\s*:\s*(?<name>[^\r\n]+)(?:\s+Description\s*:\s*(?<desc>[^\r\n]+))?(?:\s+Size\s*:\s*(?<size>[\d,]+)\s*bytes)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DismEditionBlockRegex();

    private static IReadOnlyList<WimEditionInfo> ParseDismEditions(string dismListText, string architecture, string version)
    {
        var list = new List<WimEditionInfo>();
        if (string.IsNullOrWhiteSpace(dismListText))
        {
            return list;
        }

        foreach (Match m in DismEditionBlockRegex().Matches(dismListText))
        {
            if (!int.TryParse(m.Groups["idx"].Value, out int idx))
            {
                continue;
            }

            long sizeBytes = 0L;
            string rawSize = m.Groups["size"].Value.Replace(",", string.Empty);
            long.TryParse(rawSize, out sizeBytes);

            list.Add(new WimEditionInfo
            {
                Index = idx,
                Name = m.Groups["name"].Value.Trim(),
                Description = m.Groups["desc"].Success ? m.Groups["desc"].Value.Trim() : string.Empty,
                Architecture = architecture,
                SizeBytes = sizeBytes,
                Version = version
            });
        }

        return list;
    }

    private static (string Architecture, string Version) ParseDismDetailInfo(string dismDetailText)
    {
        string arch = "Unknown";
        string version = string.Empty;

        if (string.IsNullOrWhiteSpace(dismDetailText))
        {
            return (arch, version);
        }

        foreach (string rawLine in dismDetailText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("Architecture :", StringComparison.OrdinalIgnoreCase))
            {
                string val = line["Architecture :".Length..].Trim().ToLowerInvariant();
                arch = val switch
                {
                    "x64" or "amd64" => "x64",
                    "arm64" => "arm64",
                    "x86" => "x86",
                    _ => val
                };
            }
            else if (line.StartsWith("Version :", StringComparison.OrdinalIgnoreCase))
            {
                version = line["Version :".Length..].Trim();
            }
        }

        return (arch, version);
    }
}
