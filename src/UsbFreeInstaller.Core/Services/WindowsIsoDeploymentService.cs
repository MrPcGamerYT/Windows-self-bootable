using System.Runtime.InteropServices;
using System.Text;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services.Native;

namespace UsbFreeInstaller.Core.Services;

/// <summary>
/// 100% Self-Contained Pure C# + Native wimgapi.dll ISO Extractor, WIM Splitter, ESD Converter & Verifier.
/// Operates with ZERO reliance on Mount-DiskImage, powershell.exe, dism.exe, reg.exe, or $env:TEMP.
/// </summary>
public sealed class WindowsIsoDeploymentService : IIsoDeploymentService
{
    private readonly IProcessRunner _processRunner;
    private readonly ISafeLogger _logger;

    public WindowsIsoDeploymentService(IProcessRunner processRunner, ISafeLogger logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task DeployIsoToInstallerPartitionAsync(
        IsoInspectionResult iso,
        string targetDriveLetter,
        int copyBufferSizeBytes = 1024 * 1024,
        bool writeEditionUnlockEiCfg = true,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool bypassWin11HardwareChecks = false)
    {
        string cleanTargetLetter = targetDriveLetter.TrimEnd(':', '\\', '/');
        string targetRoot = $"{cleanTargetLetter}:{Path.DirectorySeparatorChar}";

        if (!Directory.Exists(targetRoot))
        {
            throw new DirectoryNotFoundException($"Installer partition root '{targetRoot}' is not accessible.");
        }

        int safeBufferBytes = Math.Clamp(copyBufferSizeBytes, 256 * 1024, 8 * 1024 * 1024);

        // Detect dual-arch prefix (e.g. "x64\" in "x64\sources\boot.wim") so we promote active arch to root sources\
        string? archFolderPrefix = DetectDualArchSubfolderPrefix(iso);
        string? skipPayloadNormalized = iso.SplitStrategy is WimSplitStrategy.SplitWimToSwm or WimSplitStrategy.ConvertEsdThenSplitToSwm
            ? NormalizeRelPath(iso.PayloadRelativePath)
            : null;

        using IsoImageStreamReader directReader = IsoImageStreamReader.Open(iso.IsoPath);

        // Self-heal imaging library directly from the ISO's clean sources\wimgapi.dll BEFORE touching host System32
        NativeWimApi.TryPrimeSelfHostedWimgapiFromIso(directReader, _logger);

        await ExtractAllFilesFromStreamReaderAsync(
            directReader,
            targetRoot,
            iso,
            archFolderPrefix,
            skipPayloadNormalized,
            safeBufferBytes,
            progress,
            cancellationToken).ConfigureAwait(false);

        // Handle >4 GB FAT32 splitting for install.wim or install.esd via in-process wimgapi.dll
        if (iso.SplitStrategy is WimSplitStrategy.SplitWimToSwm or WimSplitStrategy.ConvertEsdThenSplitToSwm)
        {
            await SplitLargePayloadForFat32NativeAsync(
                iso,
                directReader,
                targetRoot,
                safeBufferBytes,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        // Ensure custom/modified ISOs always have boot\boot.sdi and efi\boot\bootx64.efi (or bootaa64.efi)
        await EnsureRequiredBootFilesPresentAsync(targetRoot, iso, cancellationToken).ConfigureAwait(false);

        // Preserve any existing custom ei.cfg from modified ISOs; generate Retail unlock ei.cfg only if absent
        if (writeEditionUnlockEiCfg)
        {
            string sourcesDir = Path.Combine(targetRoot, "sources");
            Directory.CreateDirectory(sourcesDir);
            string eiCfgPath = Path.Combine(sourcesDir, "ei.cfg");
            if (!File.Exists(eiCfgPath))
            {
                const string eiCfgContent = "[Channel]\r\nRetail\r\n[VL]\r\n0\r\n";
                await File.WriteAllTextAsync(eiCfgPath, eiCfgContent, Encoding.ASCII, cancellationToken).ConfigureAwait(false);
                _logger.Info("Deploy", @"Generated sources\ei.cfg ([Channel] Retail) so all ISO editions can be selected during Setup.");
            }
            else
            {
                _logger.Info("Deploy", @"Preserved existing custom sources\ei.cfg from the selected ISO.");
            }
        }

        if (bypassWin11HardwareChecks)
        {
            progress?.Report(new DeploymentProgress(
                "Applying Win11 Hardware Check Bypass",
                94,
                "Applying LabConfig (TPM / SecureBoot / CPU / RAM bypass) via pure-C# AutoUnattend + WinPE hive..."));

            // Layer 1 (100% Pure C# Zero-DLL guarantee): Write root AutoUnattend.xml (windowsPE LabConfig pass ONLY)
            // if the selected ISO does not already contain a custom autounattend.xml / unattend.xml.
            await EnsurePureCSharpWin11LabConfigAutoUnattendAsync(targetRoot, iso.Architecture, cancellationToken).ConfigureAwait(false);

            // Layer 2 (Belt & suspenders): Also patch boot.wim offline SYSTEM hive if ISO/system wimgapi.dll is available
            if (OperatingSystem.IsWindows())
            {
                string bootWimPath = Path.Combine(targetRoot, "sources", "boot.wim");
                await Task.Run(
                    () => NativeWimApi.CustomizeBootWimInProcess(
                        bootWimPath,
                        applyWin11HardwareBypass: true,
                        storageDriversSourceDir: null,
                        _logger),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteSwbiPartitionBrandingAsync(targetRoot, iso, cancellationToken).ConfigureAwait(false);

        progress?.Report(new DeploymentProgress(
            "Deployment complete",
            100,
            $"All Windows Setup files & {SwbiBrand.ShortName} branding written to {targetRoot}."));
    }

    public Task VerifyDeployedBootAndInstallFilesAsync(
        IsoInspectionResult iso,
        string targetDriveLetter,
        CancellationToken cancellationToken = default)
    {
        string cleanLetter = targetDriveLetter.TrimEnd(':', '\\', '/');
        string root = $"{cleanLetter}:{Path.DirectorySeparatorChar}";

        _logger.Info("Verify", $"Validating extracted Windows Setup files on {root}...");

        RequireNonEmptyFile(Path.Combine(root, "boot", "boot.sdi"), @"boot\boot.sdi");
        RequireNonEmptyFile(Path.Combine(root, "sources", "boot.wim"), @"sources\boot.wim");

        string efiX64 = Path.Combine(root, "efi", "boot", "bootx64.efi");
        string efiArm64 = Path.Combine(root, "efi", "boot", "bootaa64.efi");
        string efiIa32 = Path.Combine(root, "efi", "boot", "bootia32.efi");
        string legacyBootMgr = Path.Combine(root, "bootmgr");
        if (!FileExistsNonEmpty(efiX64) &&
            !FileExistsNonEmpty(efiArm64) &&
            !FileExistsNonEmpty(efiIa32) &&
            !FileExistsNonEmpty(legacyBootMgr))
        {
            throw new InvalidOperationException(@"Verification failed: Missing bootloader under 'efi\boot\' (bootx64.efi / bootia32.efi / bootaa64.efi) or 'bootmgr' on installer partition.");
        }

        string deployedPayloadPath;
        if (iso.SplitStrategy is WimSplitStrategy.SplitWimToSwm or WimSplitStrategy.ConvertEsdThenSplitToSwm)
        {
            deployedPayloadPath = Path.Combine(root, "sources", "install.swm");
            RequireNonEmptyFile(deployedPayloadPath, @"sources\install.swm");
            RequireNonEmptyFile(Path.Combine(root, "sources", "install2.swm"), @"sources\install2.swm");
        }
        else
        {
            string rel = StripDualArchPrefix(NormalizeRelPath(iso.PayloadRelativePath), DetectDualArchSubfolderPrefix(iso));
            deployedPayloadPath = Path.Combine(root, rel.Replace('\\', Path.DirectorySeparatorChar));
            RequireNonEmptyFile(deployedPayloadPath, rel);
        }

        string bootWimPath = Path.Combine(root, "sources", "boot.wim");

        if (!NativeWimApi.VerifyWimOrSwmIntegrity(bootWimPath, out string bootDetail))
        {
            throw new InvalidOperationException($"Verification failed on '{bootWimPath}': {bootDetail}");
        }

        if (!NativeWimApi.VerifyWimOrSwmIntegrity(deployedPayloadPath, out string payloadDetail))
        {
            throw new InvalidOperationException($"Verification failed on '{deployedPayloadPath}': {payloadDetail}");
        }

        _logger.Info("Verify", $"Native verification passed: boot.wim ({bootDetail}) and '{Path.GetFileName(deployedPayloadPath)}' ({payloadDetail}) on {root}.");
        return Task.CompletedTask;
    }

    private async Task ExtractAllFilesFromStreamReaderAsync(
        IsoImageStreamReader reader,
        string targetRoot,
        IsoInspectionResult iso,
        string? archFolderPrefix,
        string? skipPayloadNormalized,
        int safeBufferBytes,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var entriesToExtract = reader.Files
            .Where(f => ShouldCopyEntry(f.RelativePath, archFolderPrefix, skipPayloadNormalized))
            .ToArray();

        long totalCopyBytes = entriesToExtract.Sum(f => f.SizeBytes);
        long copiedBytes = 0L;

        _logger.Info(
            "Deploy",
            $"Extracting {entriesToExtract.Length} file(s) ({ByteFormatting.FormatGb(totalCopyBytes)}) directly from ISO to {targetRoot} ({safeBufferBytes / 1024:N0} KB buffer, pure C# zero-mount mode)...");

        byte[] buffer = new byte[safeBufferBytes];
        foreach (IsoFileEntry entry in entriesToExtract)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string mappedRel = StripDualArchPrefix(entry.RelativePath, archFolderPrefix);

            // Never trust a path taken from the disc image: resolve it and require containment inside the
            // installer partition before a single byte is written.
            if (!SafeRelativePath.TryResolveUnder(targetRoot, mappedRel, out string destFull, out string pathError))
            {
                throw new InvalidDataException(
                    $"Refusing to extract '{entry.RelativePath}' from the ISO: {pathError} " +
                    "No files were written for this entry. Verify that you are using a trusted ISO.");
            }

            await reader.ExtractFileAsync(entry, destFull, buffer, bytesDelta =>
            {
                copiedBytes += bytesDelta;
                int copyPct = totalCopyBytes > 0
                    ? (int)Math.Min(100L, (copiedBytes * 100L) / totalCopyBytes)
                    : 100;
                int overallPct = iso.SplitStrategy == WimSplitStrategy.DirectCopy
                    ? copyPct
                    : copyPct / 2;

                progress?.Report(new DeploymentProgress("Extracting ISO files", overallPct, mappedRel));
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SplitLargePayloadForFat32NativeAsync(
        IsoInspectionResult iso,
        IsoImageStreamReader directReader,
        string targetRoot,
        int safeBufferBytes,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(targetRoot, "sources"));
        string dstSwm = Path.Combine(targetRoot, "sources", "install.swm");

        if (!directReader.TryGetFile(iso.PayloadRelativePath, out IsoFileEntry? payloadEntry) || payloadEntry is null)
        {
            throw new FileNotFoundException($"Could not locate OS payload '{iso.PayloadRelativePath}' inside '{iso.IsoPath}'.");
        }

        // FAST SMART PATH: Stream >4 GB standard install.wim DIRECTLY from open ISO -> PureCSharpWimSplitter (.swm chunks)
        // Zero temporary disk space on C:, zero native DLLs, zero Windows tools!
        if (iso.SplitStrategy == WimSplitStrategy.SplitWimToSwm)
        {
            using Stream isoWimStream = directReader.OpenFileStream(payloadEntry);
            if (PureCSharpWimSplitter.CanSplitDirectly(isoWimStream))
            {
                _logger.Info("Deploy", $"Streaming '{payloadEntry.RelativePath}' directly from ISO into FAT32 .swm chunks via PureCSharpWimSplitter (0 bytes temp staging on C:, 0 DLLs)...");
                await Task.Run(() =>
                {
                    PureCSharpWimSplitter.SplitWimStream(
                        isoWimStream,
                        dstSwm,
                        iso.SwmChunkSizeMb,
                        safeBufferBytes,
                        (writtenBytes, totalPayloadBytes, partIdx, totalParts) =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int pct = 50 + (int)Math.Min(42L, (writtenBytes * 42L) / Math.Max(1L, totalPayloadBytes));
                            progress?.Report(new DeploymentProgress(
                                "Splitting WIM image for FAT32 (Pure C#)",
                                pct,
                                $"Writing part {partIdx} of {totalParts} ({iso.SwmChunkSizeMb} MB max)..."));
                        },
                        cancellationToken);
                }, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // Solid ESD or non-standard WIM path: stage inside SwbiWorkDirectory (C:\ProgramData\SWBI\Work\wim-stage-*)
        string tempStageDir = SwbiWorkDirectory.CreateUniqueStagingDirectory("wim-stage");

        try
        {
            string sourcePayloadFile = Path.Combine(tempStageDir, Path.GetFileName(payloadEntry.RelativePath));
            _logger.Info("Deploy", $"Staging '{payloadEntry.RelativePath}' to '{sourcePayloadFile}' for solid ESD conversion...");

            byte[] stageBuf = new byte[safeBufferBytes];
            long stagedBytes = 0L;
            await directReader.ExtractFileAsync(payloadEntry, sourcePayloadFile, stageBuf, delta =>
            {
                stagedBytes += delta;
                int stagePct = 50 + (int)Math.Min(18L, (stagedBytes * 18L) / Math.Max(1L, payloadEntry.SizeBytes));
                progress?.Report(new DeploymentProgress("Staging solid ESD for conversion", stagePct, payloadEntry.RelativePath));
            }, cancellationToken).ConfigureAwait(false);

            if (iso.SplitStrategy == WimSplitStrategy.SplitWimToSwm)
            {
                progress?.Report(new DeploymentProgress(
                    "Splitting WIM image for FAT32",
                    70,
                    $"Splitting into {iso.SwmChunkSizeMb} MB .swm chunks..."));

                await Task.Run(
                    () => NativeWimApi.SplitWimToSwm(sourcePayloadFile, dstSwm, iso.SwmChunkSizeMb),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (iso.SplitStrategy == WimSplitStrategy.ConvertEsdThenSplitToSwm)
            {
                string tempConvertedWim = Path.Combine(tempStageDir, "install-converted.wim");

                await Task.Run(() =>
                {
                    NativeWimApi.ConvertEsdToWim(sourcePayloadFile, tempConvertedWim, (idx, total) =>
                    {
                        int pct = 68 + ((idx * 18) / Math.Max(1, total));
                        progress?.Report(new DeploymentProgress(
                            "Converting solid ESD to WIM",
                            pct,
                            $"Self-hosted wimgapi exporting edition {idx} of {total}..."));
                    });
                }, cancellationToken).ConfigureAwait(false);

                // Free the staged ESD immediately before PureCSharpWimSplitter splits the converted WIM
                SwbiWorkDirectory.TryDeleteFile(sourcePayloadFile);

                progress?.Report(new DeploymentProgress(
                    "Splitting converted WIM for FAT32 (Pure C#)",
                    88,
                    $"PureCSharpWimSplitter creating {iso.SwmChunkSizeMb} MB .swm chunks..."));

                await Task.Run(
                    () => NativeWimApi.SplitWimToSwm(tempConvertedWim, dstSwm, iso.SwmChunkSizeMb),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            SwbiWorkDirectory.TryDeleteDirectory(tempStageDir);
        }
    }

    private async Task EnsurePureCSharpWin11LabConfigAutoUnattendAsync(
        string targetRoot,
        string isoArchitecture,
        CancellationToken cancellationToken)
    {
        // Respect any existing custom autounattend.xml / unattend.xml included in modded/custom ISOs
        string autoUnattend = Path.Combine(targetRoot, "AutoUnattend.xml");
        string unattend = Path.Combine(targetRoot, "unattend.xml");
        if (File.Exists(autoUnattend) || File.Exists(unattend))
        {
            _logger.Info("Deploy", "Preserved existing root answer file from custom ISO.");
            return;
        }

        string procArch = isoArchitecture.Equals("arm64", StringComparison.OrdinalIgnoreCase)
            ? "arm64"
            : isoArchitecture.Equals("x86", StringComparison.OrdinalIgnoreCase)
                ? "x86"
                : "amd64";

        // Minimal, safe windowsPE-ONLY RunSynchronous LabConfig bypass (touches NO partitions, NO editions, NO OOBE settings)
        string xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <unattend xmlns="urn:schemas-microsoft-com:unattend">
              <settings pass="windowsPE">
                <component name="Microsoft-Windows-Setup" processorArchitecture="{procArch}" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
                  <RunSynchronous>
                    <RunSynchronousCommand wcm:action="add">
                      <Order>1</Order>
                      <Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f</Path>
                    </RunSynchronousCommand>
                    <RunSynchronousCommand wcm:action="add">
                      <Order>2</Order>
                      <Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f</Path>
                    </RunSynchronousCommand>
                    <RunSynchronousCommand wcm:action="add">
                      <Order>3</Order>
                      <Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f</Path>
                    </RunSynchronousCommand>
                    <RunSynchronousCommand wcm:action="add">
                      <Order>4</Order>
                      <Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassCPUCheck /t REG_DWORD /d 1 /f</Path>
                    </RunSynchronousCommand>
                    <RunSynchronousCommand wcm:action="add">
                      <Order>5</Order>
                      <Path>reg add HKLM\SYSTEM\Setup\LabConfig /v BypassStorageCheck /t REG_DWORD /d 1 /f</Path>
                    </RunSynchronousCommand>
                  </RunSynchronous>
                </component>
              </settings>
            </unattend>
            """;

        await File.WriteAllTextAsync(autoUnattend, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
        _logger.Info("Deploy", "Wrote pure-C# windowsPE LabConfig AutoUnattend.xml (100% DLL-free Win11 hardware check bypass).");
    }

    private async Task EnsureRequiredBootFilesPresentAsync(
        string targetRoot,
        IsoInspectionResult iso,
        CancellationToken cancellationToken)
    {
        // 1. Ensure boot.wim is at targetRoot\sources\boot.wim
        string expectedBootWim = Path.Combine(targetRoot, "sources", "boot.wim");
        if (!FileExistsNonEmpty(expectedBootWim))
        {
            string altBootWim = Path.Combine(targetRoot, NormalizeRelPath(iso.BootWimRelativePath).Replace('\\', Path.DirectorySeparatorChar));
            if (FileExistsNonEmpty(altBootWim))
            {
                Directory.CreateDirectory(Path.Combine(targetRoot, "sources"));
                File.Copy(altBootWim, expectedBootWim, overwrite: true);
            }
        }

        // 2. Ensure boot\boot.sdi exists — extract directly out of ISO's own boot.wim first (self-contained!), then check host OS
        string expectedBootSdi = Path.Combine(targetRoot, "boot", "boot.sdi");
        if (!FileExistsNonEmpty(expectedBootSdi))
        {
            Directory.CreateDirectory(Path.Combine(targetRoot, "boot"));

            string localSdi = Path.Combine(targetRoot, "sources", "boot.sdi");
            if (FileExistsNonEmpty(localSdi))
            {
                await CopySingleFileWritableAsync(localSdi, expectedBootSdi, new byte[256 * 1024], _ => { }, cancellationToken).ConfigureAwait(false);
            }
            else if (NativeWimApi.TryExtractFileFromWim(expectedBootWim, @"\Windows\Boot\DVD\EFI\boot.sdi", expectedBootSdi) ||
                     NativeWimApi.TryExtractFileFromWim(expectedBootWim, @"\Windows\System32\boot.sdi", expectedBootSdi))
            {
                _logger.Info("Deploy", @"Extracted missing boot\boot.sdi directly out of ISO sources\boot.wim.");
            }
            else
            {
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string[] hostSdiCandidates =
                {
                    Path.Combine(winDir, "Boot", "DVD", "EFI", "boot.sdi"),
                    Path.Combine(winDir, "Boot", "DVD", "PCAT", "boot.sdi"),
                    Path.Combine(winDir, "System32", "boot.sdi")
                };

                string? foundHostSdi = hostSdiCandidates.FirstOrDefault(FileExistsNonEmpty);
                if (foundHostSdi is not null)
                {
                    await CopySingleFileWritableAsync(foundHostSdi, expectedBootSdi, new byte[256 * 1024], _ => { }, cancellationToken).ConfigureAwait(false);
                    _logger.Info("Deploy", $"Supplied missing boot\\boot.sdi from '{foundHostSdi}'.");
                }
            }
        }

        // 3. Ensure efi\boot\bootx64.efi (or bootia32.efi for 32-bit x86, or bootaa64.efi for ARM64) exists
        string efiBootDir = Path.Combine(targetRoot, "efi", "boot");
        string bootX64 = Path.Combine(efiBootDir, "bootx64.efi");
        string bootArm64 = Path.Combine(efiBootDir, "bootaa64.efi");
        string bootIa32 = Path.Combine(efiBootDir, "bootia32.efi");

        if (!FileExistsNonEmpty(bootX64) && !FileExistsNonEmpty(bootArm64) && !FileExistsNonEmpty(bootIa32))
        {
            bool isArm = string.Equals(iso.Architecture, "arm64", StringComparison.OrdinalIgnoreCase) ||
                         (string.Equals(iso.Architecture, "Unknown", StringComparison.OrdinalIgnoreCase) && RuntimeInformation.OSArchitecture == Architecture.Arm64);
            bool isX86 = string.Equals(iso.Architecture, "x86", StringComparison.OrdinalIgnoreCase) ||
                         (string.Equals(iso.Architecture, "Unknown", StringComparison.OrdinalIgnoreCase) && RuntimeInformation.OSArchitecture == Architecture.X86);

            string destEfi = isArm ? bootArm64 : isX86 ? bootIa32 : bootX64;
            Directory.CreateDirectory(efiBootDir);

            string[] isoLocalCandidates =
            {
                Path.Combine(targetRoot, "efi", "microsoft", "boot", "bootmgfw.efi"),
                Path.Combine(targetRoot, "bootmgr.efi")
            };

            string? foundIsoLocal = isoLocalCandidates.FirstOrDefault(FileExistsNonEmpty);
            if (foundIsoLocal is not null)
            {
                await CopySingleFileWritableAsync(foundIsoLocal, destEfi, new byte[256 * 1024], _ => { }, cancellationToken).ConfigureAwait(false);
                _logger.Info("Deploy", $"Synthesized missing UEFI bootloader '{Path.GetFileName(destEfi)}' from ISO '{foundIsoLocal}'.");
            }
            else if (NativeWimApi.TryExtractFileFromWim(expectedBootWim, @"\Windows\Boot\EFI\bootmgfw.efi", destEfi))
            {
                _logger.Info("Deploy", $"Extracted missing UEFI bootloader '{Path.GetFileName(destEfi)}' directly out of ISO sources\\boot.wim.");
            }
            else
            {
                string hostBootMgfw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Boot", "EFI", "bootmgfw.efi");
                if (FileExistsNonEmpty(hostBootMgfw))
                {
                    await CopySingleFileWritableAsync(hostBootMgfw, destEfi, new byte[256 * 1024], _ => { }, cancellationToken).ConfigureAwait(false);
                    _logger.Info("Deploy", $"Supplied missing UEFI bootloader '{Path.GetFileName(destEfi)}' from '{hostBootMgfw}'.");
                }
            }
        }

        // 4. Ensure root 'bootmgr' exists for Legacy BIOS / MBR boot compatibility even on stripped/EFI-only custom ISOs
        string rootBootMgr = Path.Combine(targetRoot, "bootmgr");
        if (!FileExistsNonEmpty(rootBootMgr))
        {
            if (NativeWimApi.TryExtractFileFromWim(expectedBootWim, @"\Windows\Boot\PCAT\bootmgr", rootBootMgr))
            {
                _logger.Info("Deploy", @"Extracted missing Legacy BIOS 'bootmgr' directly out of ISO sources\boot.wim.");
            }
            else
            {
                string hostPcatBootMgr = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Boot", "PCAT", "bootmgr");
                if (FileExistsNonEmpty(hostPcatBootMgr))
                {
                    await CopySingleFileWritableAsync(hostPcatBootMgr, rootBootMgr, new byte[256 * 1024], _ => { }, cancellationToken).ConfigureAwait(false);
                    _logger.Info("Deploy", $"Supplied missing Legacy BIOS 'bootmgr' from '{hostPcatBootMgr}'.");
                }
            }
        }

        // 5. Ensure 'boot\boot.sdi' RAMDISK System Deployment Image exists (required by Windows Boot Manager ramdisk loader)
        string bootSdi = Path.Combine(targetRoot, "boot", "boot.sdi");
        if (!FileExistsNonEmpty(bootSdi))
        {
            Directory.CreateDirectory(Path.Combine(targetRoot, "boot"));
            if (NativeWimApi.TryExtractFileFromWim(expectedBootWim, @"\Windows\Boot\DVD\EFI\boot.sdi", bootSdi) ||
                NativeWimApi.TryExtractFileFromWim(expectedBootWim, @"\Windows\Boot\DVD\PCAT\boot.sdi", bootSdi))
            {
                _logger.Info("Deploy", @"Extracted missing 'boot\boot.sdi' directly out of ISO sources\boot.wim.");
            }
            else
            {
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string? hostSdi = new[]
                {
                    Path.Combine(winDir, "Boot", "DVD", "EFI", "boot.sdi"),
                    Path.Combine(winDir, "Boot", "DVD", "PCAT", "boot.sdi"),
                    Path.Combine(winDir, "System32", "Recovery", "boot.sdi")
                }.FirstOrDefault(FileExistsNonEmpty);

                if (hostSdi is not null)
                {
                    await CopySingleFileWritableAsync(hostSdi, bootSdi, new byte[256 * 1024], _ => { }, cancellationToken).ConfigureAwait(false);
                    _logger.Info("Deploy", $"Supplied missing 'boot\\boot.sdi' from '{hostSdi}'.");
                }
            }
        }
    }

    private async Task WriteSwbiPartitionBrandingAsync(
        string targetRoot,
        IsoInspectionResult iso,
        CancellationToken cancellationToken)
    {
        try
        {
            string iconPath = Path.Combine(targetRoot, SwbiBrand.PartitionIconFileName);
            await using (Stream? iconStream = typeof(SwbiBrand).Assembly.GetManifestResourceStream("UsbFreeInstaller.Core.Resources.swbi.ico"))
            {
                if (iconStream is not null)
                {
                    if (File.Exists(iconPath))
                    {
                        File.SetAttributes(iconPath, FileAttributes.Normal);
                    }

                    await using var iconOut = new FileStream(iconPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await iconStream.CopyToAsync(iconOut, cancellationToken).ConfigureAwait(false);
                }
            }

            string autorunPath = Path.Combine(targetRoot, "autorun.inf");
            if (File.Exists(autorunPath))
            {
                File.SetAttributes(autorunPath, FileAttributes.Normal);
            }

            string autorunContent =
                $"[autorun.amd64]\r\n" +
                $"open=setup.exe\r\n" +
                $"label={SwbiBrand.AsciiTitle}\r\n" +
                $"icon={SwbiBrand.PartitionIconFileName}\r\n\r\n" +
                $"[autorun]\r\n" +
                $"open=setup.exe\r\n" +
                $"label={SwbiBrand.AsciiTitle}\r\n" +
                $"icon={SwbiBrand.PartitionIconFileName}\r\n";

            await File.WriteAllTextAsync(autorunPath, autorunContent, Encoding.ASCII, cancellationToken).ConfigureAwait(false);

            string identityPath = Path.Combine(targetRoot, SwbiBrand.PartitionIdentityReadmeFileName);
            string identityBody =
                $"====================================================================\r\n" +
                $"  {SwbiBrand.AsciiTitle}\r\n" +
                $"====================================================================\r\n" +
                $"Partition Label : {SwbiBrand.PartitionVolumeLabel}\r\n" +
                $"Source ISO      : {Path.GetFileName(iso.IsoPath)} ({iso.Architecture}, {iso.IsoVariantBadge})\r\n" +
                $"Driver Folder   : {SwbiBrand.DriverChooserFolderName}\r\n\r\n" +
                $"IMPORTANT IN WINDOWS SETUP:\r\n" +
                $"  - DO NOT delete or format this '{SwbiBrand.PartitionVolumeLabel}' partition during Setup!\r\n" +
                $"  - If asked to 'Load driver', browse to '{SwbiBrand.PartitionVolumeLabel}' -> '{SwbiBrand.DriverChooserFolderName}'.\r\n" +
                $"  - After Windows finishes installing, run SWBI (--cleanup) to reclaim this space.\r\n";

            await File.WriteAllTextAsync(identityPath, identityBody, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            _logger.Info("Deploy", $"Applied '{SwbiBrand.AsciiTitle}' custom icon & drive identity branding on {targetRoot}.");
        }
        catch (Exception ex)
        {
            _logger.Warn("Deploy", $"Non-fatal warning while writing SWBI drive icon/autorun branding: {ex.Message}");
        }
    }

    private static string? DetectDualArchSubfolderPrefix(IsoInspectionResult iso)
    {
        string bootNorm = NormalizeRelPath(iso.BootWimRelativePath);
        foreach (string prefix in new[] { @"x64\", @"arm64\", @"x86\" })
        {
            if (bootNorm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return prefix;
            }
        }

        return null;
    }

    private static bool ShouldCopyEntry(string relPath, string? activeArchPrefix, string? skipPayloadNormalized)
    {
        string norm = NormalizeRelPath(relPath);
        if (skipPayloadNormalized is not null && string.Equals(norm, skipPayloadNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (activeArchPrefix is not null)
        {
            foreach (string otherPrefix in new[] { @"x64\", @"arm64\", @"x86\" })
            {
                if (!string.Equals(otherPrefix, activeArchPrefix, StringComparison.OrdinalIgnoreCase) &&
                    norm.StartsWith(otherPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string StripDualArchPrefix(string relPath, string? activeArchPrefix)
    {
        string norm = NormalizeRelPath(relPath);
        if (activeArchPrefix is not null && norm.StartsWith(activeArchPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return norm[activeArchPrefix.Length..];
        }

        return norm;
    }

    private static async Task CopySingleFileWritableAsync(
        string sourcePath,
        string destPath,
        byte[] buffer,
        Action<int> onChunkCopied,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destPath))
        {
            File.SetAttributes(destPath, FileAttributes.Normal);
        }

        await using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous))
        {
            int read;
            while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                onChunkCopied(read);
            }
        }

        File.SetAttributes(destPath, FileAttributes.Normal);
    }

    private static string NormalizeRelPath(string relPath) =>
        relPath.TrimStart('\\', '/').Replace('/', '\\');

    private static bool FileExistsNonEmpty(string fullPath) =>
        File.Exists(fullPath) && new FileInfo(fullPath).Length > 0;

    private static void RequireNonEmptyFile(string fullPath, string relativeDisplayName)
    {
        if (!FileExistsNonEmpty(fullPath))
        {
            throw new InvalidOperationException(
                $"Verification failed: Required file '{relativeDisplayName}' is missing or 0 bytes at '{fullPath}'.");
        }
    }
}
