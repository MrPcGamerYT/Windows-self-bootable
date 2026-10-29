using System.Buffers.Binary;

namespace UsbFreeInstaller.Core.Services.Native;

internal readonly record struct NtfsBootSectorInfo(int BytesPerSector, long TotalSectors)
{
    internal long LengthBytes => checked(TotalSectors * BytesPerSector);
}

internal static class NtfsBootSector
{
    internal static NtfsBootSectorInfo Parse(ReadOnlySpan<byte> bootSector)
    {
        if (bootSector.Length < 512 ||
            !bootSector.Slice(3, 8).SequenceEqual("NTFS    "u8) ||
            bootSector[510] != 0x55 || bootSector[511] != 0xAA)
        {
            throw new InvalidOperationException("The volume does not have a readable NTFS boot sector.");
        }

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(11, 2));
        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(bootSector.Slice(40, 8));
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096) || totalSectors <= 0)
        {
            throw new InvalidOperationException("The NTFS boot sector contains invalid geometry.");
        }

        var result = new NtfsBootSectorInfo(bytesPerSector, totalSectors);
        try
        {
            _ = result.LengthBytes;
        }
        catch (OverflowException error)
        {
            throw new InvalidOperationException("The NTFS boot sector contains an overflowing volume size.", error);
        }

        return result;
    }

    internal static void VerifyFits(NtfsBootSectorInfo boot, NtfsVolumeGeometry geometry, long partitionBytes)
    {
        if (partitionBytes <= 0 || boot.BytesPerSector != geometry.BytesPerSector ||
            geometry.TotalSectors <= 0 || boot.TotalSectors != geometry.TotalSectors ||
            boot.LengthBytes > partitionBytes)
        {
            throw new InvalidOperationException(
                "NTFS boot-sector and volume geometry could not be verified inside the partition. " +
                "No installer partition will be created.");
        }
    }
}
