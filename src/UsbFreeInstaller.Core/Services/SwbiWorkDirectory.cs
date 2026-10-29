using System.Runtime.InteropServices;
using System.Text;

namespace UsbFreeInstaller.Core.Services;

/// <summary>
/// Resolves a guaranteed-existing, 8.3-short-name-immune, space-safe working directory for SWBI.
/// On tweaked or SSD-installed Windows systems where NtfsDisable8dot3NameCreation=1 and the username
/// contains spaces (e.g. 'C:\Users\Z LAG'), $env:TEMP / Path.GetTempPath() often points to a non-existent
/// 8.3 path ('C:\Users\ZLAG~1\AppData\Local\Temp'). SWBI never relies on $env:TEMP blindly.
/// </summary>
public static class SwbiWorkDirectory
{
    public static string EnsureSafeWorkDirectory(string? subFolder = null)
    {
        string baseDir = ResolveBaseWorkDirectory();
        string full = string.IsNullOrWhiteSpace(subFolder)
            ? baseDir
            : Path.Combine(baseDir, subFolder);

        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>
    /// Ensures the current process's Win32 and .NET TEMP/TMP environment variables point to an existing,
    /// writable, 8.3-safe directory so WPF OpenFileDialog, shader caches, and BCL helpers never fail on
    /// broken 'C:\Users\ZLAG~1\AppData\Local\Temp' paths.
    /// </summary>
    public static string EnsureProcessTempVariablesHealthy()
    {
        string? curTemp = Environment.GetEnvironmentVariable("TEMP");
        if (!string.IsNullOrWhiteSpace(curTemp) &&
            !ContainsBrokenShort8Dot3Segment(curTemp) &&
            Directory.Exists(curTemp) &&
            TryVerifyWritableDirectory(curTemp))
        {
            return curTemp;
        }

        string safeTemp = EnsureSafeWorkDirectory("temp");
        Environment.SetEnvironmentVariable("TEMP", safeTemp);
        Environment.SetEnvironmentVariable("TMP", safeTemp);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SetEnvironmentVariableW("TEMP", safeTemp);
                SetEnvironmentVariableW("TMP", safeTemp);
            }
            catch
            {
                // Ignore if P/Invoke restricted
            }
        }

        return safeTemp;
    }

    public static string CreateUniqueStagingDirectory(string prefix = "stage")
    {
        string dir = Path.Combine(EnsureSafeWorkDirectory(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDeleteDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directoryPath))
            {
                foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch
                    {
                        // Ignore attribute reset error
                    }
                }

                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // Never throw from cleanup finally blocks
        }
    }

    public static void TryDeleteFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.SetAttributes(filePath, FileAttributes.Normal);
                File.Delete(filePath);
            }
        }
        catch
        {
            // Never throw from cleanup finally blocks
        }
    }

    private static string ResolveBaseWorkDirectory()
    {
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            // Priority 1: C:\ProgramData\SWBI\Work (space-free, 8.3-free, standard machine-wide path)
            string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(commonAppData))
            {
                candidates.Add(Path.Combine(NormalizeLongPath(commonAppData), "SWBI", "Work"));
            }

            // Priority 2: <SystemDrive>:\SWBI-Work
            string sysDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\', '/');
            if (!string.IsNullOrWhiteSpace(sysDrive))
            {
                candidates.Add($"{sysDrive}{Path.DirectorySeparatorChar}SWBI-Work");
            }

            // Priority 3: LocalApplicationData (real long profile path, not $env:TEMP 8.3 path)
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(NormalizeLongPath(localAppData), "SWBI", "Work"));
            }
        }

        // Cross-platform / unit-test fallback
        string rawTemp = Path.GetTempPath();
        if (!string.IsNullOrWhiteSpace(rawTemp))
        {
            candidates.Add(Path.Combine(NormalizeLongPath(rawTemp), "SWBI-Work"));
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryVerifyWritableDirectory(candidate))
            {
                return candidate;
            }
        }

        string fallback = Path.Combine(AppContext.BaseDirectory, "SWBI-Work");
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch
        {
            // Ignore read-only base directory
        }
        return fallback;
    }

    public static bool ContainsBrokenShort8Dot3Segment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.Contains('~'))
        {
            return false;
        }

        string[] parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        string? running = null;

        foreach (string part in parts)
        {
            if (running is null)
            {
                running = part.EndsWith(':') ? part + "\\" : part;
                continue;
            }

            running = Path.Combine(running, part);

            int tildeIdx = part.IndexOf('~');
            if (tildeIdx > 0 && tildeIdx + 1 < part.Length && char.IsDigit(part[tildeIdx + 1]))
            {
                if (!Directory.Exists(running) && !File.Exists(running))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryVerifyWritableDirectory(string dirPath)
    {
        if (ContainsBrokenShort8Dot3Segment(dirPath))
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

            string probe = Path.Combine(dirPath, $".probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, new byte[] { 0x53 });
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeLongPath(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.Contains('~'))
        {
            return path;
        }

        try
        {
            var sb = new StringBuilder(1024);
            uint len = GetLongPathNameW(path, sb, (uint)sb.Capacity);
            if (len > 0 && len < sb.Capacity)
            {
                return sb.ToString();
            }
        }
        catch
        {
            // Keep original path if GetLongPathNameW cannot resolve nonexistent 8.3 segment
        }

        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool SetEnvironmentVariableW(string lpName, string? lpValue);
}
