using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace UsbFreeInstaller.Launcher;

/// <summary>
/// 100% NativeAOT Win32 Machine-Code Bootstrapper for SWBI — Self Windows Bootable Installer.
/// Why this exists:
///   1. Standard .NET PublishSingleFile WPF apps use Microsoft's C++ singlefilehost.exe, which calls GetTempPathW()
///      BEFORE .NET or Main() ever starts. On SSD Windows installs with spaces in the username (e.g. 'C:\Users\Z LAG')
///      and 8.3 short names disabled, %TEMP% points to 'C:\Users\ZLAG~1\AppData\Local\Temp' (non-existent), causing
///      singlefilehost.exe to abort silently with 0x80008093 before any window appears.
///   2. Hardcoded requireAdministrator manifests fail completely on corrupted/stripped Windows builds where the UAC
///      'Appinfo' service was disabled or damaged.
/// This pure NativeAOT executable imports only core Win32 DLLs, requires ZERO %TEMP% extraction to start, repairs
/// TEMP/TMP/DOTNET_BUNDLE_EXTRACT_BASE_DIR to a verified 8.3-free path, performs smart UAC self-elevation with
/// automatic Dry-Run fallback if UAC is broken, and launches the unbundled WPF application cleanly.
/// </summary>
internal static class Program
{
    private const string EmbeddedPayloadResourceName = "SWBI.Payload.zip";
    private const string InnerUiExeName = "SWBI-SelfWindowsBootableInstaller.exe";
    private const string ReadyMarkerFileName = ".swbi-ready";
    private const string ElevatedRelaunchFlag = "--elevated-relaunch";

    private static readonly Guid FolderIdProgramData = new("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97");
    private static readonly Guid FolderIdLocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_OK = 0x00000000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevationClass = 20;

    [STAThread]
    private static int Main(string[] args)
    {
        bool isSmokeTest = HasArg(args, "--smoke-test") || HasArg(args, "/smoke-test");
        bool skipElevation = isSmokeTest ||
                             HasArg(args, "--no-elevate") ||
                             HasArg(args, ElevatedRelaunchFlag);

        try
        {
            // 1. Smart UAC Self-Elevation: prompt UAC automatically on normal launch,
            //    but NEVER fail to open if UAC/Appinfo is disabled on a corrupted/stripped Windows OS!
            if (!skipElevation && !IsCurrentProcessElevated())
            {
                if (TryRelaunchSelfAsAdministrator(args))
                {
                    return 0;
                }
            }

            // 2. Resolve a 100% verified writable, 8.3-short-name-immune runtime base directory
            string safeBaseDir = ResolveVerifiedSafeRuntimeDirectory();
            string safeTempDir = Path.Combine(safeBaseDir, "temp");
            string safeBundleDir = Path.Combine(safeBaseDir, "bundle");
            Directory.CreateDirectory(safeTempDir);
            Directory.CreateDirectory(safeBundleDir);

            // 3. Repair Win32 + .NET process environment block BEFORE any .NET child process starts
            SetEnvironmentVariableW("TEMP", safeTempDir);
            SetEnvironmentVariableW("TMP", safeTempDir);
            SetEnvironmentVariableW("DOTNET_BUNDLE_EXTRACT_BASE_DIR", safeBundleDir);
            Environment.SetEnvironmentVariable("TEMP", safeTempDir);
            Environment.SetEnvironmentVariable("TMP", safeTempDir);
            Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", safeBundleDir);

            // 4. Extract (or reuse cached) unbundled WPF application payload
            string uiExePath = ResolveOrExtractUiExecutable(safeBaseDir);

            // 5. Launch SWBI WPF UI forwarding user arguments
            return LaunchAndMonitorUiProcess(uiExePath, args, isSmokeTest, safeBaseDir);
        }
        catch (Exception ex)
        {
            if (!isSmokeTest)
            {
                MessageBoxW(
                    IntPtr.Zero,
                    $"SWBI Launcher encountered a startup error:\n\n{ex.GetType().Name}: {ex.Message}",
                    "SWBI — Self Windows Bootable Installer",
                    MB_OK | MB_ICONERROR);
            }
            return 3;
        }
    }

    private static string ResolveOrExtractUiExecutable(string safeBaseDir)
    {
        using Stream? payloadStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedPayloadResourceName);
        if (payloadStream is null)
        {
            // Dev fallback: check sibling 'app' subfolder
            string? selfDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(selfDir))
            {
                string siblingExe = Path.Combine(selfDir, "app", InnerUiExeName);
                if (File.Exists(siblingExe))
                {
                    return siblingExe;
                }
            }

            throw new FileNotFoundException("Embedded SWBI application payload ('SWBI.Payload.zip') was not found in launcher binary.");
        }

        byte[] hashBytes = SHA256.HashData(payloadStream);
        payloadStream.Position = 0;
        string hashHex = Convert.ToHexString(hashBytes, 0, 6).ToLowerInvariant();

        string appFolderName = $"app-1.0.0-{hashHex}";
        string targetAppDir = Path.Combine(safeBaseDir, appFolderName);
        string targetExePath = Path.Combine(targetAppDir, InnerUiExeName);
        string readyMarkerPath = Path.Combine(targetAppDir, ReadyMarkerFileName);

        if (File.Exists(readyMarkerPath) && File.Exists(targetExePath))
        {
            return targetExePath;
        }

        Directory.CreateDirectory(targetAppDir);

        byte[] copyBuf = new byte[128 * 1024];
        using (var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string relPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(relPath))
                {
                    continue;
                }

                string destPath = Path.GetFullPath(Path.Combine(targetAppDir, relPath));
                if (!destPath.StartsWith(Path.GetFullPath(targetAppDir), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                string? parentDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                if (File.Exists(destPath))
                {
                    try { File.SetAttributes(destPath, FileAttributes.Normal); } catch { }
                }

                using Stream entryIn = entry.Open();
                using var fileOut = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                int read;
                while ((read = entryIn.Read(copyBuf, 0, copyBuf.Length)) > 0)
                {
                    fileOut.Write(copyBuf, 0, read);
                }
            }
        }

        if (!File.Exists(targetExePath))
        {
            throw new FileNotFoundException($"Extracted SWBI runtime is missing '{InnerUiExeName}' inside '{targetAppDir}'.");
        }

        File.WriteAllText(readyMarkerPath, hashHex, Encoding.ASCII);

        // Best-effort cleanup of older cached app-* folders
        CleanupOlderAppCaches(safeBaseDir, appFolderName);

        return targetExePath;
    }

    private static int LaunchAndMonitorUiProcess(
        string uiExePath,
        string[] rawArgs,
        bool waitUntilExit,
        string safeBaseDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = uiExePath,
            WorkingDirectory = Path.GetDirectoryName(uiExePath) ?? safeBaseDir,
            UseShellExecute = false
        };

        foreach (string arg in rawArgs)
        {
            if (!string.Equals(arg, ElevatedRelaunchFlag, StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add(arg);
            }
        }

        if (!waitUntilExit && HasArg(rawArgs, "--wait"))
        {
            waitUntilExit = true;
        }

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            throw new InvalidOperationException($"Windows failed to start '{uiExePath}'.");
        }

        if (waitUntilExit)
        {
            proc.WaitForExit();
            return proc.ExitCode;
        }

        // Monitor initial 2.5 seconds so an early crash is surfaced clearly instead of failing silently
        if (proc.WaitForExit(2500) && proc.ExitCode != 0)
        {
            MessageBoxW(
                IntPtr.Zero,
                $"SWBI exited unexpectedly during startup (code {proc.ExitCode}).\n\nCheck logs under:\nC:\\ProgramData\\SWBI\\Work\\logs",
                "SWBI — Self Windows Bootable Installer",
                MB_OK | MB_ICONERROR);
            return proc.ExitCode;
        }

        return 0;
    }

    private static string ResolveVerifiedSafeRuntimeDirectory()
    {
        var candidates = new List<string>(5);

        // 1. Real KnownFolder ProgramData (e.g. C:\ProgramData\SWBI\Runtime)
        string? progData = TryGetKnownFolderLongPath(FolderIdProgramData);
        if (!string.IsNullOrWhiteSpace(progData))
        {
            candidates.Add(Path.Combine(progData, "SWBI", "Runtime"));
        }

        // 2. Real KnownFolder LocalAppData (returns true long NTFS path e.g. 'C:\Users\Z LAG\AppData\Local', NEVER 'ZLAG~1'!)
        string? localApp = TryGetKnownFolderLongPath(FolderIdLocalAppData);
        if (!string.IsNullOrWhiteSpace(localApp))
        {
            candidates.Add(Path.Combine(localApp, "SWBI", "Runtime"));
        }

        // 3. SystemDrive root fallback (e.g. C:\SWBI-Runtime)
        string sysDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\', '/');
        if (!string.IsNullOrWhiteSpace(sysDrive))
        {
            candidates.Add($"{sysDrive}\\SWBI-Runtime");
        }

        // 4. Directory alongside the downloaded .exe itself
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(exeDir))
        {
            candidates.Add(Path.Combine(exeDir, "SWBI-Runtime"));
        }

        foreach (string candidate in candidates)
        {
            if (TryVerifyDirectoryWritableAnd8Dot3Safe(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Could not locate any writable directory on disk to stage SWBI runtime files.");
    }

    private static bool TryVerifyDirectoryWritableAnd8Dot3Safe(string dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath) || ContainsNonExistent8Dot3Segment(dirPath))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(dirPath);
            if (!Directory.Exists(dirPath))
            {
                return false;
            }

            string probeFile = Path.Combine(dirPath, $".probe-{Environment.ProcessId}-{ Environment.TickCount64 }.tmp");
            File.WriteAllBytes(probeFile, new byte[] { 0x53, 0x57, 0x42, 0x49 });
            File.Delete(probeFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsNonExistent8Dot3Segment(string path)
    {
        if (!path.Contains('~'))
        {
            return false;
        }

        string[] segments = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        string? running = null;

        foreach (string seg in segments)
        {
            if (running is null)
            {
                running = seg.EndsWith(':') ? seg + "\\" : seg;
                continue;
            }

            running = Path.Combine(running, seg);
            int tilde = seg.IndexOf('~');
            if (tilde > 0 && tilde + 1 < seg.Length && char.IsDigit(seg[tilde + 1]))
            {
                if (!Directory.Exists(running) && !File.Exists(running))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void CleanupOlderAppCaches(string safeBaseDir, string keepFolderName)
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(safeBaseDir, "app-*"))
            {
                if (!string.Equals(Path.GetFileName(dir), keepFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
        }
        catch
        {
            // Ignore locked older caches
        }
    }

    private static string? TryGetKnownFolderLongPath(Guid folderId)
    {
        try
        {
            if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out IntPtr pPath) == 0 && pPath != IntPtr.Zero)
            {
                try
                {
                    string? raw = Marshal.PtrToStringUni(pPath);
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        return null;
                    }

                    var sb = new StringBuilder(1024);
                    uint n = GetLongPathNameW(raw, sb, (uint)sb.Capacity);
                    return (n > 0 && n < sb.Capacity) ? sb.ToString() : raw;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pPath);
                }
            }
        }
        catch
        {
            // Ignore if shell API unavailable on minimal WinPE
        }

        return null;
    }

    private static bool IsCurrentProcessElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr hToken) || hToken == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return GetTokenInformation(hToken, TokenElevationClass, out uint elevated, 4, out _) && elevated != 0;
        }
        finally
        {
            CloseHandle(hToken);
        }
    }

    private static bool TryRelaunchSelfAsAdministrator(string[] args)
    {
        string? selfExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(selfExe) || !File.Exists(selfExe))
        {
            return false;
        }

        var paramSb = new StringBuilder();
        paramSb.Append(ElevatedRelaunchFlag);
        foreach (string a in args)
        {
            paramSb.Append(' ');
            paramSb.Append(QuoteWindowsArg(a));
        }

        IntPtr pVerb = Marshal.StringToCoTaskMemUni("runas");
        IntPtr pFile = Marshal.StringToCoTaskMemUni(selfExe);
        IntPtr pParams = Marshal.StringToCoTaskMemUni(paramSb.ToString());
        IntPtr pDir = Marshal.StringToCoTaskMemUni(Path.GetDirectoryName(selfExe) ?? string.Empty);

        try
        {
            var sei = new SHELLEXECUTEINFOW_BLITTABLE
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW_BLITTABLE>(),
                fMask = 0,
                hwnd = IntPtr.Zero,
                lpVerb = pVerb,
                lpFile = pFile,
                lpParameters = pParams,
                lpDirectory = pDir,
                nShow = 1 // SW_SHOWNORMAL
            };

            return ShellExecuteExW(ref sei);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pVerb);
            Marshal.FreeCoTaskMem(pFile);
            Marshal.FreeCoTaskMem(pParams);
            Marshal.FreeCoTaskMem(pDir);
        }
    }

    private static string QuoteWindowsArg(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        if (!arg.Contains(' ') && !arg.Contains('"') && !arg.Contains('\t'))
        {
            return arg;
        }

        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    private static bool HasArg(string[] args, string flag)
    {
        foreach (string a in args)
        {
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHELLEXECUTEINFOW_BLITTABLE
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpFile;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public IntPtr lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW_BLITTABLE lpExecInfo);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool SetEnvironmentVariableW(string lpName, string? lpValue);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        out uint TokenInformation,
        int TokenInformationLength,
        out int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
}
