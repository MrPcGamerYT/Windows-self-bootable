using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace UsbFreeInstaller.Core.Services.Updates;

public enum StandaloneFlavor { X64, X64Lightweight, Arm64, X86 }

public sealed record StandaloneLauncherIdentity(string Path, StandaloneFlavor Flavor);

/// <summary>Identifies the downloaded native SFX, not the extracted WPF child process.</summary>
public static class StandaloneLauncher
{
    public const string LauncherPathEnvironmentVariable = "SWBI_STANDALONE_LAUNCHER";

    public static StandaloneLauncherIdentity FromEnvironment()
    {
        string? path = Environment.GetEnvironmentVariable(LauncherPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Updates require launching the downloaded SWBI standalone EXE, not the extracted app or a development build.");
        StandaloneLauncherIdentity identity = Read(path);
        EnsureMatchesProcess(identity.Flavor, RuntimeInformation.ProcessArchitecture);
        return identity;
    }

    public static StandaloneLauncherIdentity Read(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.IsPathFullyQualified(fullPath) || !fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The standalone launcher path is not a regular EXE.");

        using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 112) throw new InvalidOperationException("The standalone launcher is truncated.");
        Span<byte> mz = stackalloc byte[64];
        file.ReadExactly(mz);
        if (mz[0] != 'M' || mz[1] != 'Z') throw new InvalidOperationException("The standalone launcher is not a PE executable.");
        uint peOffset = BinaryPrimitives.ReadUInt32LittleEndian(mz[60..64]);
        Span<byte> trailer = stackalloc byte[16];
        file.Position = file.Length - trailer.Length;
        file.ReadExactly(trailer);
        if (!trailer[8..].SequenceEqual("SWBIEOF1"u8)) throw new InvalidOperationException("The standalone launcher has no SWBI payload trailer.");
        ulong payloadOffset = BinaryPrimitives.ReadUInt64LittleEndian(trailer[..8]);
        if (payloadOffset > (ulong)(file.Length - 48) || peOffset < 64 || (ulong)peOffset + 6 > payloadOffset)
            throw new InvalidOperationException("The standalone launcher has an invalid payload offset.");
        Span<byte> pe = stackalloc byte[6];
        file.Position = peOffset;
        file.ReadExactly(pe);
        if (!pe[..4].SequenceEqual("PE\0\0"u8)) throw new InvalidOperationException("The standalone launcher has an invalid PE header.");
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]);

        Span<byte> header = stackalloc byte[32];
        file.Position = (long)payloadOffset;
        file.ReadExactly(header);
        if (!header[..8].SequenceEqual("SWBIPK01"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]) is 0 or > 10000)
            throw new InvalidOperationException("The standalone launcher has an invalid payload header.");
        string archiveId = Encoding.ASCII.GetString(header[8..24]).TrimEnd('\0');
        int dash = archiveId.IndexOf('-');
        if (dash <= 0 || archiveId.Length != dash + 9 || !archiveId[(dash + 1)..].All(Uri.IsHexDigit))
            throw new InvalidOperationException("The standalone launcher has an invalid flavor identifier.");
        StandaloneFlavor flavor = archiveId[..dash] switch
        {
            "x64s" => StandaloneFlavor.X64,
            "x64l" => StandaloneFlavor.X64Lightweight,
            "arm64" => StandaloneFlavor.Arm64,
            "x86s" => StandaloneFlavor.X86,
            _ => throw new InvalidOperationException("The standalone launcher flavor is not recognized.")
        };
        ushort expectedMachine = flavor switch
        {
            StandaloneFlavor.X64 or StandaloneFlavor.X64Lightweight => 0x8664,
            StandaloneFlavor.Arm64 => 0xAA64,
            StandaloneFlavor.X86 => 0x014C,
            _ => 0
        };
        if (machine != expectedMachine) throw new InvalidOperationException("The standalone launcher architecture does not match its flavor.");
        return new StandaloneLauncherIdentity(fullPath, flavor);
    }

    internal static void EnsureMatchesProcess(StandaloneFlavor flavor, Architecture architecture)
    {
        bool matches = flavor switch
        {
            StandaloneFlavor.X64 or StandaloneFlavor.X64Lightweight => architecture == Architecture.X64,
            StandaloneFlavor.Arm64 => architecture == Architecture.Arm64,
            StandaloneFlavor.X86 => architecture == Architecture.X86,
            _ => false
        };
        if (!matches) throw new InvalidOperationException("The launcher flavor does not match the running SWBI process.");
    }
}
