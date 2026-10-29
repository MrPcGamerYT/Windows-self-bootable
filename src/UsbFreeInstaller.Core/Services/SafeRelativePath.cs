namespace UsbFreeInstaller.Core.Services;

/// <summary>
/// Validates ISO-supplied relative paths before they are combined with an installer-partition root.
/// ISO-9660/Joliet and UDF directory records can legally contain names such as <c>..</c>, drive-relative
/// names (<c>C:</c>), alternate data stream separators and reserved DOS device names. Combining any of
/// those with <see cref="Path.Combine(string, string)"/> would let a crafted, corrupted or merely unusual
/// custom ISO write outside the target partition — including over the running host OS — so every extracted
/// path is resolved and containment-checked before any bytes are written.
/// </summary>
public static class SafeRelativePath
{
    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Resolves <paramref name="relativePath"/> beneath <paramref name="rootDirectory"/> and guarantees the
    /// result stays inside that root. Returns <c>false</c> with a user-safe reason for anything suspicious.
    /// </summary>
    public static bool TryResolveUnder(
        string rootDirectory,
        string relativePath,
        out string fullPath,
        out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            error = "The extraction root directory is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "The disc image contains an entry with an empty path.";
            return false;
        }

        string normalized = relativePath.Trim().Replace('/', '\\');

        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = $"'{relativePath}' is a UNC network path.";
            return false;
        }

        normalized = normalized.TrimStart('\\');
        if (normalized.Length == 0)
        {
            error = $"'{relativePath}' does not name a file.";
            return false;
        }

        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            error = $"'{relativePath}' is a drive-absolute path, not a relative entry.";
            return false;
        }

        foreach (string segment in normalized.Split('\\'))
        {
            if (segment.Length == 0)
            {
                continue; // Tolerate doubled separators, which some mastering tools emit.
            }

            if (segment is "." or "..")
            {
                error = $"'{relativePath}' contains a '{segment}' traversal segment.";
                return false;
            }

            if (segment.IndexOf(':') >= 0)
            {
                error = $"'{relativePath}' contains ':' (drive or alternate data stream separator).";
                return false;
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = $"'{relativePath}' contains characters that are not valid in a file name.";
                return false;
            }

            string stem = segment.Split('.')[0].TrimEnd(' ');
            if (Array.Exists(ReservedDeviceNames, name => string.Equals(name, stem, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"'{relativePath}' uses the reserved Windows device name '{stem}'.";
                return false;
            }
        }

        string rootFull;
        try
        {
            rootFull = Path.GetFullPath(rootDirectory);
        }
        catch (Exception ex)
        {
            error = $"The extraction root could not be resolved: {ex.Message}";
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(rootFull, normalized));
        }
        catch (Exception ex)
        {
            error = $"'{relativePath}' could not be resolved to a file path: {ex.Message}";
            return false;
        }

        if (!IsUnder(rootFull, candidate))
        {
            error = $"'{relativePath}' resolves outside the target partition.";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>True when the path contains a <c>.</c> or <c>..</c> directory segment.</summary>
    public static bool ContainsTraversalSegment(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (string segment in path.Split('\\', '/'))
        {
            if (segment is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnder(string rootFull, string candidate)
    {
        string root = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 0)
        {
            // The root itself is a filesystem root (e.g. "/"), so require the separator prefix below.
            root = rootFull;
        }

        if (candidate.Length <= root.Length)
        {
            return false;
        }

        string prefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
