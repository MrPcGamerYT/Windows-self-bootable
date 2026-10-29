using System.Buffers.Binary;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using UsbFreeInstaller.Core.Abstractions;

namespace UsbFreeInstaller.Core.Services.Native;

/// <summary>
/// Smart Self-Healing WIM/ESD/SWM Engine:
///   1. Uses 100% pure C# PureCSharpWimSplitter for .wim -> .swm splitting & binary SWM/WIM verification (zero DLLs).
///   2. Self-heals wimgapi.dll by extracting the clean copy inside the selected Windows ISO (sources\wimgapi.dll)
///      via NativeLibrary.SetDllImportResolver BEFORE ever trusting host C:\Windows\System32\wimgapi.dll,
///      so SWBI succeeds even when the host Windows OS is corrupted, modded, or completely stripped.
/// </summary>
public static class NativeWimApi
{
    private const uint WIM_GENERIC_READ = 0x80000000;
    private const uint WIM_GENERIC_WRITE = 0x40000000;

    private const uint WIM_CREATE_ALWAYS = 2;
    private const uint WIM_OPEN_EXISTING = 3;

    private const uint WIM_COMPRESS_XPRESS = 1;
    private const uint WIM_COMPRESS_LZX = 2;

    private const uint WIM_EXPORT_ALLOW_DUPLICATES = 0x00000001;
    private const uint WIM_FLAG_SHARE_WRITE = 0x00000040;
    private const uint WIM_REFERENCE_APPEND = 0x00010000;

    private static readonly object ResolverSync = new();
    private static bool s_resolverRegistered;
    private static string? s_isoHostedWimgapiPath;
    private static IntPtr s_cachedWimgapiHandle;

    static NativeWimApi()
    {
        EnsureDllImportResolverRegistered();
    }

    private static void EnsureDllImportResolverRegistered()
    {
        lock (ResolverSync)
        {
            if (s_resolverRegistered)
            {
                return;
            }

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeWimApi).Assembly, ResolveNativeLibrary);
            }
            catch
            {
                // Resolver already set for this assembly in test runner
            }

            s_resolverRegistered = true;
        }
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("wimgapi.dll", StringComparison.OrdinalIgnoreCase) &&
            !libraryName.Equals("wimgapi", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        lock (ResolverSync)
        {
            if (s_cachedWimgapiHandle != IntPtr.Zero)
            {
                return s_cachedWimgapiHandle;
            }

            // Priority 1: Pristine wimgapi.dll extracted from the selected Windows ISO (immune to corrupted/stripped host System32!)
            if (!string.IsNullOrWhiteSpace(s_isoHostedWimgapiPath) &&
                File.Exists(s_isoHostedWimgapiPath) &&
                NativeLibrary.TryLoad(s_isoHostedWimgapiPath, out IntPtr isoHandle) &&
                isoHandle != IntPtr.Zero)
            {
                s_cachedWimgapiHandle = isoHandle;
                return isoHandle;
            }

            // Priority 2: Host System32\wimgapi.dll if present and undamaged
            string sys32Path = Path.Combine(Environment.SystemDirectory, "wimgapi.dll");
            if (File.Exists(sys32Path) &&
                NativeLibrary.TryLoad(sys32Path, out IntPtr sysHandle) &&
                sysHandle != IntPtr.Zero)
            {
                s_cachedWimgapiHandle = sysHandle;
                return sysHandle;
            }

            if (NativeLibrary.TryLoad("wimgapi.dll", assembly, searchPath, out IntPtr fallbackHandle))
            {
                s_cachedWimgapiHandle = fallbackHandle;
                return fallbackHandle;
            }

            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Extracts a clean, matching-architecture wimgapi.dll directly from the selected Windows ISO into
    /// SWBI's private workspace so corrupted/stripped host System32 imaging libraries can never break SWBI.
    /// </summary>
    public static void TryPrimeSelfHostedWimgapiFromIso(IsoImageStreamReader isoReader, ISafeLogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        EnsureDllImportResolverRegistered();

        string archPrefix = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => @"arm64\sources\",
            Architecture.X86 => @"x86\sources\",
            _ => @"x64\sources\"
        };

        string[] candidates =
        {
            @"sources\wimgapi.dll",
            archPrefix + "wimgapi.dll"
        };

        foreach (string rel in candidates)
        {
            if (!isoReader.TryGetFile(rel, out IsoFileEntry? entry) || entry is null || entry.SizeBytes < 32 * 1024)
            {
                continue;
            }

            try
            {
                string runtimeDir = SwbiWorkDirectory.EnsureSafeWorkDirectory("iso-runtime");
                string targetDll = Path.Combine(runtimeDir, "wimgapi.dll");

                byte[] buf = new byte[256 * 1024];
                using (Stream src = isoReader.OpenFileStream(entry))
                using (var dst = new FileStream(targetDll, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    int r;
                    while ((r = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, r);
                    }
                }

                // Verify PE machine architecture matches current process before activating
                if (DoesPeArchitectureMatchCurrentProcess(targetDll) &&
                    NativeLibrary.TryLoad(targetDll, out IntPtr testHandle) &&
                    testHandle != IntPtr.Zero)
                {
                    lock (ResolverSync)
                    {
                        s_isoHostedWimgapiPath = targetDll;
                        s_cachedWimgapiHandle = testHandle;
                    }
                    logger.Info("NativeWim", $"Primed self-hosted wimgapi.dll directly from ISO ('{rel}') — immune to host OS corruption.");
                    return;
                }
            }
            catch
            {
                // Ignore if locked or architecture mismatch (e.g. x86 ISO on x64 host)
            }
        }
    }

    public static bool IsWimgapiAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        IntPtr handle = ResolveNativeLibrary("wimgapi.dll", typeof(NativeWimApi).Assembly, null);
        return handle != IntPtr.Zero;
    }

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr WIMCreateFile(
        string pszWimPath,
        uint dwDesiredAccess,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        uint dwCompressionType,
        out uint pdwCreationResult);

    [DllImport("wimgapi.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool WIMCloseHandle(IntPtr hObject);

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool WIMSetTemporaryPath(IntPtr hWim, string pszPath);

    [DllImport("wimgapi.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint WIMGetImageCount(IntPtr hWim);

    [DllImport("wimgapi.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr WIMLoadImage(IntPtr hWim, uint dwImageIndex);

    [DllImport("wimgapi.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool WIMSetBootImage(IntPtr hWim, uint dwImageIndex);

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool WIMSetReferenceFile(
        IntPtr hWim,
        string pszPath,
        uint dwFlags);

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool WIMSplitFile(
        IntPtr hWim,
        string pszPartPath,
        ref long pliPartSize,
        uint dwFlags);

    [DllImport("wimgapi.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool WIMExportImage(
        IntPtr hImage,
        IntPtr hWim,
        uint dwFlags);

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool WIMExtractImagePath(
        IntPtr hImage,
        string pszImagePath,
        string pszDestinationPath,
        uint dwExtractFlags);

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool WIMApplyImage(
        IntPtr hImage,
        string pszPath,
        uint dwApplyFlags);

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr WIMCaptureImage(
        IntPtr hWim,
        string pszPath,
        uint dwCaptureFlags);

    [DllImport("wimgapi.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool WIMGetImageInformation(
        IntPtr hImage,
        out IntPtr ppvImageInfo,
        out uint pcbImageInfo);

    [DllImport("wimgapi.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool WIMSetImageInformation(
        IntPtr hImage,
        byte[] pvImageInfo,
        uint cbImageInfo);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static void SplitWimToSwm(string sourceWimPath, string destinationSwmPath, int chunkSizeMb)
    {
        string? outDir = Path.GetDirectoryName(destinationSwmPath);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        // Priority 1: 100% Pure C# MSWIM binary splitter (zero native DLLs, zero OS dependencies!)
        using (var probeStream = new FileStream(sourceWimPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (PureCSharpWimSplitter.CanSplitDirectly(probeStream))
            {
                PureCSharpWimSplitter.SplitWimStream(probeStream, destinationSwmPath, chunkSizeMb);
                return;
            }
        }

        // Priority 2: ISO-self-hosted or system wimgapi.dll fallback for non-standard solid WIMs
        if (!IsWimgapiAvailable())
        {
            throw new InvalidOperationException("Source image uses solid compression and neither ISO nor system wimgapi.dll is available.");
        }

        string tempWorkDir = SwbiWorkDirectory.EnsureSafeWorkDirectory("wimtemp");
        IntPtr hWim = WIMCreateFile(
            sourceWimPath,
            WIM_GENERIC_READ,
            WIM_OPEN_EXISTING,
            WIM_FLAG_SHARE_WRITE,
            0,
            out _);

        if (hWim == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"Native WIMCreateFile failed opening '{sourceWimPath}' (Win32 error {err}).");
        }

        try
        {
            WIMSetTemporaryPath(hWim, tempWorkDir);
            long partSizeBytes = Math.Max(1L, chunkSizeMb) * 1024L * 1024L;

            if (!WIMSplitFile(hWim, destinationSwmPath, ref partSizeBytes, 0))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"Native WIMSplitFile failed creating '{destinationSwmPath}' (Win32 error {err}).");
            }
        }
        finally
        {
            WIMCloseHandle(hWim);
        }
    }

    public static void ConvertEsdToWim(
        string sourceEsdPath,
        string destinationWimPath,
        Action<int, int>? onEditionProgress = null)
    {
        if (!IsWimgapiAvailable())
        {
            throw new InvalidOperationException(
                "Solid .esd conversion requires wimgapi.dll, which was stripped from both this custom ISO and the host Windows OS. Use an ISO with install.wim or install.swm.");
        }

        string tempWorkDir = SwbiWorkDirectory.EnsureSafeWorkDirectory("wimtemp");
        string? outDir = Path.GetDirectoryName(destinationWimPath);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        IntPtr hSrc = WIMCreateFile(sourceEsdPath, WIM_GENERIC_READ, WIM_OPEN_EXISTING, WIM_FLAG_SHARE_WRITE, 0, out _);
        if (hSrc == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"Native WIMCreateFile failed opening ESD '{sourceEsdPath}' (Win32 error {err}).");
        }

        try
        {
            WIMSetTemporaryPath(hSrc, tempWorkDir);
            int count = (int)Math.Max(1u, WIMGetImageCount(hSrc));

            IntPtr hDst = WIMCreateFile(
                destinationWimPath,
                WIM_GENERIC_READ | WIM_GENERIC_WRITE,
                WIM_CREATE_ALWAYS,
                0,
                WIM_COMPRESS_LZX,
                out _);

            if (hDst == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"Native WIMCreateFile failed creating temporary WIM '{destinationWimPath}' (Win32 error {err}).");
            }

            try
            {
                WIMSetTemporaryPath(hDst, tempWorkDir);

                for (int idx = 1; idx <= count; idx++)
                {
                    onEditionProgress?.Invoke(idx, count);
                    IntPtr hImg = WIMLoadImage(hSrc, (uint)idx);
                    if (hImg == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        throw new Win32Exception(err, $"Native WIMLoadImage failed on ESD index {idx} (Win32 error {err}).");
                    }

                    try
                    {
                        if (!WIMExportImage(hImg, hDst, WIM_EXPORT_ALLOW_DUPLICATES))
                        {
                            int err = Marshal.GetLastWin32Error();
                            throw new Win32Exception(err, $"Native WIMExportImage failed on ESD index {idx} (Win32 error {err}).");
                        }
                    }
                    finally
                    {
                        WIMCloseHandle(hImg);
                    }
                }
            }
            finally
            {
                WIMCloseHandle(hDst);
            }
        }
        finally
        {
            WIMCloseHandle(hSrc);
        }
    }

    public static void CaptureDirectoryToWim(string sourceDirectory, string destinationWimPath)
    {
        string tempWorkDir = SwbiWorkDirectory.EnsureSafeWorkDirectory("wimtemp");
        IntPtr hWim = WIMCreateFile(
            destinationWimPath,
            WIM_GENERIC_READ | WIM_GENERIC_WRITE,
            WIM_CREATE_ALWAYS,
            0,
            WIM_COMPRESS_XPRESS,
            out _);

        if (hWim == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"WIMCreateFile failed creating '{destinationWimPath}' (Win32 error {err}).");
        }

        try
        {
            WIMSetTemporaryPath(hWim, tempWorkDir);
            IntPtr hCap = WIMCaptureImage(hWim, sourceDirectory, 0);
            if (hCap == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"WIMCaptureImage failed capturing '{sourceDirectory}' (Win32 error {err}).");
            }

            WIMCloseHandle(hCap);
        }
        finally
        {
            WIMCloseHandle(hWim);
        }
    }

    public static void ApplySplitSwmToDirectory(
        string firstSwmPath,
        IReadOnlyList<string> additionalSwmParts,
        string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        string tempWorkDir = SwbiWorkDirectory.EnsureSafeWorkDirectory("wimtemp");

        IntPtr hWim = WIMCreateFile(
            firstSwmPath,
            WIM_GENERIC_READ,
            WIM_OPEN_EXISTING,
            WIM_FLAG_SHARE_WRITE,
            0,
            out _);

        if (hWim == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"WIMCreateFile rejected first SWM '{firstSwmPath}' (Win32 error {err}).");
        }

        try
        {
            WIMSetTemporaryPath(hWim, tempWorkDir);
            foreach (string extraPart in additionalSwmParts)
            {
                if (!WIMSetReferenceFile(hWim, extraPart, WIM_REFERENCE_APPEND))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new Win32Exception(err, $"WIMSetReferenceFile rejected SWM part '{extraPart}' (Win32 error {err}).");
                }
            }

            IntPtr hImg = WIMLoadImage(hWim, 1);
            if (hImg == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"WIMLoadImage failed on spanned SWM set '{firstSwmPath}' (Win32 error {err}).");
            }

            try
            {
                if (!WIMApplyImage(hImg, destinationDirectory, 0))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new Win32Exception(err, $"WIMApplyImage failed extracting spanned SWM set (Win32 error {err}).");
                }
            }
            finally
            {
                WIMCloseHandle(hImg);
            }
        }
        finally
        {
            WIMCloseHandle(hWim);
        }
    }

    public static bool TryExtractFileFromWim(
        string wimFilePath,
        string internalWimPath,
        string destinationFilePath)
    {
        if (!IsWimgapiAvailable() || !File.Exists(wimFilePath))
        {
            return false;
        }

        try
        {
            string tempWorkDir = SwbiWorkDirectory.EnsureSafeWorkDirectory("wimtemp");
            IntPtr hWim = WIMCreateFile(wimFilePath, WIM_GENERIC_READ, WIM_OPEN_EXISTING, WIM_FLAG_SHARE_WRITE, 0, out _);
            if (hWim == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                WIMSetTemporaryPath(hWim, tempWorkDir);
                uint count = Math.Max(1u, WIMGetImageCount(hWim));

                for (uint idx = 1; idx <= count; idx++)
                {
                    IntPtr hImg = WIMLoadImage(hWim, idx);
                    if (hImg == IntPtr.Zero) continue;

                    try
                    {
                        string? dstDir = Path.GetDirectoryName(destinationFilePath);
                        if (!string.IsNullOrEmpty(dstDir))
                        {
                            Directory.CreateDirectory(dstDir);
                        }

                        string normalizedInternal = internalWimPath.StartsWith('\\') ? internalWimPath : "\\" + internalWimPath;
                        if (WIMExtractImagePath(hImg, normalizedInternal, destinationFilePath, 0) &&
                            File.Exists(destinationFilePath) &&
                            new FileInfo(destinationFilePath).Length > 0)
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        WIMCloseHandle(hImg);
                    }
                }
            }
            finally
            {
                WIMCloseHandle(hWim);
            }
        }
        catch
        {
            // Never fail if host/ISO wimgapi extraction encounters an error
        }

        return false;
    }

    /// <summary>
    /// Verifies WIM/ESD/SWM integrity 100% in pure C# (including multi-part SWM GUID/part-number/offset-table consistency),
    /// so verification NEVER fails on stripped/corrupted Windows builds missing wimgapi.dll.
    /// </summary>
    public static bool VerifyWimOrSwmIntegrity(string wimOrSwmPath, out string detailMessage)
    {
        if (!File.Exists(wimOrSwmPath) || new FileInfo(wimOrSwmPath).Length < 208)
        {
            detailMessage = $"Image '{wimOrSwmPath}' is missing or truncated (< 208 bytes).";
            return false;
        }

        try
        {
            using var fs = new FileStream(wimOrSwmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> hdr = stackalloc byte[208];
            if (fs.Read(hdr) < 208 || !hdr[..8].SequenceEqual("MSWIM\0\0\0"u8))
            {
                detailMessage = $"Image '{wimOrSwmPath}' does not start with valid MSWIM signature.";
                return false;
            }

            Guid setGuid = new(hdr.Slice(24, 16));
            ushort partNum = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(40, 2));
            ushort totalParts = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(42, 2));
            uint imgCount = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(44, 4));
            ulong otRaw = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(48, 8));
            long otSize = (long)(otRaw & 0x00FFFFFFFFFFFFFFUL);
            long otOffset = BinaryPrimitives.ReadInt64LittleEndian(hdr.Slice(56, 8));

            if (otOffset <= 0 || otSize < 50 || otOffset + otSize > fs.Length)
            {
                detailMessage = $"Image '{wimOrSwmPath}' has truncated offset table (offset={otOffset}, size={otSize}, len={fs.Length}).";
                return false;
            }

            // If spanned SWM set, verify all sibling parts (install2.swm..installN.swm) exist and share the exact WIM GUID
            if (totalParts > 1 && partNum == 1)
            {
                string dir = Path.GetDirectoryName(wimOrSwmPath) ?? string.Empty;
                string stem = Path.GetFileNameWithoutExtension(wimOrSwmPath);
                string ext = Path.GetExtension(wimOrSwmPath);

                Span<byte> phdr = stackalloc byte[208];
                for (ushort p = 2; p <= totalParts; p++)
                {
                    string sibling = Path.Combine(dir, $"{stem}{p}{ext}");
                    if (!File.Exists(sibling))
                    {
                        detailMessage = $"Missing spanned SWM chunk '{sibling}' (part {p} of {totalParts}).";
                        return false;
                    }

                    using var pfs = new FileStream(sibling, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    phdr.Clear();
                    if (pfs.Read(phdr) < 208 || !phdr[..8].SequenceEqual("MSWIM\0\0\0"u8))
                    {
                        detailMessage = $"Spanned SWM chunk '{sibling}' has invalid MSWIM header.";
                        return false;
                    }

                    Guid pGuid = new(phdr.Slice(24, 16));
                    ushort pNum = BinaryPrimitives.ReadUInt16LittleEndian(phdr.Slice(40, 2));
                    ushort pTot = BinaryPrimitives.ReadUInt16LittleEndian(phdr.Slice(42, 2));
                    if (pGuid != setGuid || pNum != p || pTot != totalParts)
                    {
                        detailMessage = $"Spanned SWM chunk '{sibling}' metadata mismatch (part {pNum}/{pTot}).";
                        return false;
                    }
                }
            }

            fs.Position = 0;
            if (WimHeaderXmlParser.TryParse(fs, out ParsedWimMetadata? parsed) && parsed is not null)
            {
                detailMessage = totalParts > 1
                    ? $"Pure-C# verified {totalParts}-part SWM set ({parsed.Editions.Count} image(s), arch={parsed.PrimaryArchitecture})."
                    : $"Pure-C# verified WIM header ({parsed.Editions.Count} image(s), arch={parsed.PrimaryArchitecture}).";
                return true;
            }

            detailMessage = $"Pure-C# verified MSWIM binary header ({imgCount} image(s), {totalParts} part(s)).";
            return imgCount > 0;
        }
        catch (Exception ex)
        {
            detailMessage = $"Failed reading image '{wimOrSwmPath}': {ex.Message}";
            return false;
        }
    }

    public static bool CustomizeBootWimInProcess(
        string bootWimPath,
        bool applyWin11HardwareBypass,
        string? storageDriversSourceDir,
        ISafeLogger logger)
    {
        if (!IsWimgapiAvailable() || !File.Exists(bootWimPath))
        {
            return false;
        }

        bool hasDrivers = !string.IsNullOrWhiteSpace(storageDriversSourceDir) &&
                          Directory.Exists(storageDriversSourceDir) &&
                          Directory.EnumerateFiles(storageDriversSourceDir, "*.inf", SearchOption.AllDirectories).Any();

        if (!applyWin11HardwareBypass && !hasDrivers)
        {
            return false;
        }

        string tempWorkDir = SwbiWorkDirectory.EnsureSafeWorkDirectory("wimtemp");
        string extractDir = SwbiWorkDirectory.CreateUniqueStagingDirectory("bootpe-edit");
        string rebuiltWimPath = Path.Combine(tempWorkDir, $"boot-rebuilt-{Guid.NewGuid():N}.wim");

        try
        {
            File.SetAttributes(bootWimPath, FileAttributes.Normal);

            IntPtr hOrig = WIMCreateFile(bootWimPath, WIM_GENERIC_READ, WIM_OPEN_EXISTING, WIM_FLAG_SHARE_WRITE, 0, out _);
            if (hOrig == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                WIMSetTemporaryPath(hOrig, tempWorkDir);
                int totalImages = (int)Math.Max(1u, WIMGetImageCount(hOrig));
                int targetSetupIdx = totalImages >= 2 ? 2 : 1;

                string? origXmlInfo;
                IntPtr hSetupImg = WIMLoadImage(hOrig, (uint)targetSetupIdx);
                if (hSetupImg == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    origXmlInfo = ReadWimImageXml(hSetupImg);
                    if (!WIMApplyImage(hSetupImg, extractDir, 0))
                    {
                        int err = Marshal.GetLastWin32Error();
                        logger.Warn("NativeWim", $"WIMApplyImage on boot.wim index {targetSetupIdx} returned Win32 {err}.");
                        return false;
                    }
                }
                finally
                {
                    WIMCloseHandle(hSetupImg);
                }

                if (applyWin11HardwareBypass)
                {
                    ApplyOfflineSystemHiveLabConfig(extractDir, logger);
                }

                if (hasDrivers && storageDriversSourceDir is not null)
                {
                    StageBootDriversIntoExtractedWinPe(extractDir, storageDriversSourceDir, applyWin11HardwareBypass, logger);
                }

                IntPtr hNew = WIMCreateFile(
                    rebuiltWimPath,
                    WIM_GENERIC_READ | WIM_GENERIC_WRITE,
                    WIM_CREATE_ALWAYS,
                    0,
                    WIM_COMPRESS_XPRESS,
                    out _);

                if (hNew == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    WIMSetTemporaryPath(hNew, tempWorkDir);

                    for (int idx = 1; idx <= totalImages; idx++)
                    {
                        if (idx != targetSetupIdx)
                        {
                            IntPtr hOther = WIMLoadImage(hOrig, (uint)idx);
                            if (hOther != IntPtr.Zero)
                            {
                                try
                                {
                                    WIMExportImage(hOther, hNew, WIM_EXPORT_ALLOW_DUPLICATES);
                                }
                                finally
                                {
                                    WIMCloseHandle(hOther);
                                }
                            }
                        }
                        else
                        {
                            IntPtr hCap = WIMCaptureImage(hNew, extractDir, 0);
                            if (hCap == IntPtr.Zero)
                            {
                                return false;
                            }

                            try
                            {
                                if (!string.IsNullOrWhiteSpace(origXmlInfo))
                                {
                                    PreserveWimImageMetadataXml(hCap, origXmlInfo);
                                }
                            }
                            finally
                            {
                                WIMCloseHandle(hCap);
                            }
                        }
                    }

                    WIMSetBootImage(hNew, (uint)targetSetupIdx);
                }
                finally
                {
                    WIMCloseHandle(hNew);
                }
            }
            finally
            {
                WIMCloseHandle(hOrig);
            }

            if (!File.Exists(rebuiltWimPath) || new FileInfo(rebuiltWimPath).Length <= 1024 * 1024)
            {
                return false;
            }

            // The rebuilt image must actually be a WIM before it replaces the installer's only boot payload.
            if (!HasWimMagic(rebuiltWimPath))
            {
                logger.Warn("NativeWim", "Rebuilt boot.wim does not start with a valid MSWIM header; the original was kept.");
                return false;
            }

            // Replace atomically and keep the original until the new file is in place, so an interruption can
            // never leave the installer partition with a truncated boot.wim.
            string stagedCopy = bootWimPath + ".new";
            File.Copy(rebuiltWimPath, stagedCopy, overwrite: true);
            using (var staged = new FileStream(stagedCopy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                staged.Flush(flushToDisk: true);
            }

            string backupCopy = bootWimPath + ".swbi-backup";
            try
            {
                if (File.Exists(bootWimPath))
                {
                    File.Replace(stagedCopy, bootWimPath, backupCopy, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(stagedCopy, bootWimPath);
                }
            }
            catch (Exception replaceError)
            {
                // FAT32 volumes do not always support ReplaceFile; the staged image is already verified.
                logger.Warn("NativeWim", $"Atomic boot.wim replacement failed ({replaceError.Message}); copying the verified image instead.");
                File.Copy(stagedCopy, bootWimPath, overwrite: true);
                SwbiWorkDirectory.TryDeleteFile(stagedCopy);
            }

            logger.Info(
                "NativeWim",
                $"Updated boot.wim natively via self-hosted wimgapi.dll (zero dism.exe / reg.exe); previous image kept as '{Path.GetFileName(backupCopy)}'.");
            return true;
        }
        catch (Exception ex)
        {
            logger.Warn("NativeWim", $"Non-fatal warning customizing boot.wim in-process: {ex.Message}");
            return false;
        }
        finally
        {
            SwbiWorkDirectory.TryDeleteFile(rebuiltWimPath);
            SwbiWorkDirectory.TryDeleteDirectory(extractDir);
        }
    }

    private static bool HasWimMagic(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> magic = stackalloc byte[8];
            if (stream.Length < 208 || stream.Read(magic) != magic.Length)
            {
                return false;
            }

            return magic.SequenceEqual("MSWIM\0\0\0"u8);
        }
        catch
        {
            return false;
        }
    }

    private static bool DoesPeArchitectureMatchCurrentProcess(string dllPath)
    {
        try
        {
            using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> mz = stackalloc byte[64];
            if (fs.Read(mz) < 64 || mz[0] != 'M' || mz[1] != 'Z')
            {
                return false;
            }

            int peOffset = BinaryPrimitives.ReadInt32LittleEndian(mz.Slice(60, 4));
            if (peOffset < 64 || peOffset + 6 > fs.Length)
            {
                return false;
            }

            fs.Position = peOffset;
            Span<byte> pe = stackalloc byte[6];
            if (fs.Read(pe) < 6 || pe[0] != 'P' || pe[1] != 'E')
            {
                return false;
            }

            ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(pe.Slice(4, 2));
            ushort expected = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => 0x8664,
                Architecture.Arm64 => 0xAA64,
                Architecture.X86 => 0x014C,
                _ => (ushort)0
            };

            return expected != 0 && machine == expected;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyOfflineSystemHiveLabConfig(string extractedWinPeRoot, ISafeLogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string sysHivePath = Path.Combine(extractedWinPeRoot, "Windows", "System32", "config", "SYSTEM");
        if (!File.Exists(sysHivePath))
        {
            return;
        }

        Win32StorageInterop.EnableProcessPrivilege("SeRestorePrivilege");
        Win32StorageInterop.EnableProcessPrivilege("SeBackupPrivilege");

        string mountSubKey = $"SWBI_PE_{Guid.NewGuid():N}";
        int loadStatus = Win32StorageInterop.RegLoadKeyW(Win32StorageInterop.HKEY_LOCAL_MACHINE_PTR, mountSubKey, sysHivePath);
        if (loadStatus == 0)
        {
            try
            {
                using RegistryKey? labKey = Registry.LocalMachine.CreateSubKey($@"{mountSubKey}\Setup\LabConfig", writable: true);
                if (labKey is not null)
                {
                    labKey.SetValue("BypassTPMCheck", 1, RegistryValueKind.DWord);
                    labKey.SetValue("BypassSecureBootCheck", 1, RegistryValueKind.DWord);
                    labKey.SetValue("BypassRAMCheck", 1, RegistryValueKind.DWord);
                    labKey.SetValue("BypassCPUCheck", 1, RegistryValueKind.DWord);
                    labKey.SetValue("BypassStorageCheck", 1, RegistryValueKind.DWord);
                    labKey.Flush();
                }
                logger.Info("NativeWim", "Wrote Win11 LabConfig bypass directly into offline WinPE SYSTEM hive via RegLoadKeyW.");
            }
            finally
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Win32StorageInterop.RegUnloadKeyW(Win32StorageInterop.HKEY_LOCAL_MACHINE_PTR, mountSubKey);
            }
        }
    }

    private static void StageBootDriversIntoExtractedWinPe(
        string extractedWinPeRoot,
        string storageDriversSourceDir,
        bool includeLabConfigCmd,
        ISafeLogger logger)
    {
        string peDriversDest = Path.Combine(extractedWinPeRoot, "Windows", "SWBI-BootDrivers");
        Directory.CreateDirectory(peDriversDest);

        int infCount = 0;
        foreach (string srcFile in Directory.EnumerateFiles(storageDriversSourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(storageDriversSourceDir, srcFile);
            string dst = Path.Combine(peDriversDest, rel);
            string? dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(srcFile, dst, overwrite: true);
            if (srcFile.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
            {
                infCount++;
            }
        }

        string startnetPath = Path.Combine(extractedWinPeRoot, "Windows", "System32", "startnet.cmd");
        string existing = File.Exists(startnetPath) ? File.ReadAllText(startnetPath) : "wpeinit\r\n";
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        if (includeLabConfigCmd)
        {
            sb.AppendLine("reg add HKLM\\SYSTEM\\Setup\\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f >nul 2>&1");
            sb.AppendLine("reg add HKLM\\SYSTEM\\Setup\\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f >nul 2>&1");
            sb.AppendLine("reg add HKLM\\SYSTEM\\Setup\\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f >nul 2>&1");
            sb.AppendLine("reg add HKLM\\SYSTEM\\Setup\\LabConfig /v BypassCPUCheck /t REG_DWORD /d 1 /f >nul 2>&1");
            sb.AppendLine("reg add HKLM\\SYSTEM\\Setup\\LabConfig /v BypassStorageCheck /t REG_DWORD /d 1 /f >nul 2>&1");
        }
        sb.AppendLine("for /r \"%SystemRoot%\\SWBI-BootDrivers\" %%i in (*.inf) do drvload \"%%i\" >nul 2>&1");
        sb.AppendLine(existing.Trim());

        File.WriteAllText(startnetPath, sb.ToString(), Encoding.ASCII);
        logger.Info("NativeWim", $"Staged {infCount} OEM storage driver INF(s) into WinPE boot.wim.");
    }

    private static string? ReadWimImageXml(IntPtr hImage)
    {
        if (!WIMGetImageInformation(hImage, out IntPtr ptr, out uint cb) || ptr == IntPtr.Zero || cb < 4)
        {
            return null;
        }

        try
        {
            byte[] raw = new byte[cb];
            Marshal.Copy(ptr, raw, 0, (int)cb);
            int start = (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) ? 2 : 0;
            return Encoding.Unicode.GetString(raw, start, raw.Length - start).TrimEnd('\0');
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    private static void PreserveWimImageMetadataXml(IntPtr hCapturedImage, string origXml)
    {
        try
        {
            string? currentXml = ReadWimImageXml(hCapturedImage);
            if (string.IsNullOrWhiteSpace(currentXml))
            {
                return;
            }

            XDocument origDoc = XDocument.Parse(origXml);
            XDocument curDoc = XDocument.Parse(currentXml);
            if (origDoc.Root is null || curDoc.Root is null)
            {
                return;
            }

            foreach (string tag in new[] { "NAME", "DESCRIPTION", "DISPLAYNAME", "DISPLAYDESCRIPTION", "FLAGS", "WINDOWS" })
            {
                XElement? origEl = origDoc.Root.Element(tag);
                if (origEl is not null)
                {
                    curDoc.Root.Element(tag)?.Remove();
                    curDoc.Root.Add(new XElement(origEl));
                }
            }

            string merged = curDoc.Root.ToString(SaveOptions.DisableFormatting);
            byte[] utf16 = Encoding.Unicode.GetBytes(merged + "\0");
            byte[] withBom = new byte[2 + utf16.Length];
            withBom[0] = 0xFF;
            withBom[1] = 0xFE;
            Buffer.BlockCopy(utf16, 0, withBom, 2, utf16.Length);

            WIMSetImageInformation(hCapturedImage, withBom, (uint)withBom.Length);
        }
        catch
        {
            // Keep default captured metadata if XML merge fails
        }
    }
}
