namespace UsbFreeInstaller.Core.Models;

public enum InstallPayloadKind
{
    None = 0,
    Wim = 1,
    Esd = 2,
    Swm = 3
}

public enum WimSplitStrategy
{
    /// <summary>
    /// Payload is strictly below the 4 GiB FAT32 file limit and can be copied directly.
    /// </summary>
    DirectCopy = 0,

    /// <summary>
    /// sources\install.wim (or custom WIM) exceeds the safe FAT32 file limit and will be split via DISM /Split-Image into install.swm, install2.swm, ...
    /// </summary>
    SplitWimToSwm = 1,

    /// <summary>
    /// sources\install.esd (or custom ESD) exceeds the safe FAT32 file limit; must be exported to a temporary WIM on C: then split into .swm chunks.
    /// </summary>
    ConvertEsdThenSplitToSwm = 2,

    /// <summary>
    /// Payload is missing or cannot fit on a native Windows FAT32 volume.
    /// </summary>
    Unsupported = 3
}

public sealed record WimEditionInfo
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Architecture { get; init; } = "x64";
    public long SizeBytes { get; init; }
    public string Version { get; init; } = string.Empty;
}

public sealed record IsoInspectionResult
{
    /// <summary>
    /// Safe threshold (4,000 MB = 4,194,304,000 bytes) comfortably below FAT32's 4,294,967,295-byte max file ceiling.
    /// </summary>
    public const long Fat32SafeSingleFileLimitBytes = 4_000L * 1024L * 1024L;

    /// <summary>
    /// Chunk size in MB passed to DISM /Split-Image /FileSize:3800.
    /// </summary>
    public const int DefaultSwmChunkSizeMb = 3800;

    public required string IsoPath { get; init; }
    public required bool Exists { get; init; }
    public long FileSizeBytes { get; init; }
    public string? Sha256Hex { get; init; }
    public bool HasBootSdi { get; init; }
    public bool HasBootWim { get; init; }
    public bool HasEfiBootloader { get; init; }
    public bool HasSetupExe { get; init; }
    public bool IsCustomOrModifiedIso { get; init; }
    public string BootWimRelativePath { get; init; } = @"sources\boot.wim";
    public string BootSdiRelativePath { get; init; } = @"boot\boot.sdi";
    public string Architecture { get; init; } = "Unknown";
    public InstallPayloadKind PayloadKind { get; init; } = InstallPayloadKind.None;
    public string PayloadRelativePath { get; init; } = string.Empty;
    public long PayloadSizeBytes { get; init; }
    public WimSplitStrategy SplitStrategy { get; init; } = WimSplitStrategy.Unsupported;
    public int SwmChunkSizeMb { get; init; } = DefaultSwmChunkSizeMb;
    public IReadOnlyList<WimEditionInfo> Editions { get; init; } = Array.Empty<WimEditionInfo>();
    public IReadOnlyList<string> ValidationErrors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ValidationWarnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supports both Official Microsoft ISOs and Custom/Lite/Modified/Dual-Arch Windows 10 &amp; 11 ISOs:
    /// requires a valid boot.wim + OS install payload (.wim / .esd / .swm). Missing root setup.exe, boot.sdi,
    /// or efi\boot\bootx64.efi in custom ISOs are handled/synthesized automatically during deployment.
    /// </summary>
    public bool IsValid =>
        Exists &&
        HasBootWim &&
        PayloadKind != InstallPayloadKind.None &&
        SplitStrategy != WimSplitStrategy.Unsupported &&
        ValidationErrors.Count == 0;

    public string IsoVariantBadge => IsCustomOrModifiedIso ? "Custom / Modified ISO" : "Standard / Official ISO";

    public string FileSizeDisplay => ByteFormatting.FormatGb(FileSizeBytes);
    public string PayloadSizeDisplay => ByteFormatting.FormatGb(PayloadSizeBytes);
}
