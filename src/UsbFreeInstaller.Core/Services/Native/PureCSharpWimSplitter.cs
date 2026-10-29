using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace UsbFreeInstaller.Core.Services.Native;

/// <summary>
/// 100% Pure Managed C# MSWIM (v1.13) binary .wim -> .swm streaming splitter.
/// Splits >4 GB install.wim images directly from an ISO stream onto the FAT32 SWBI-SETUP partition
/// in a single forward pass — requiring ZERO dism.exe, ZERO wimgapi.dll, and ZERO temporary disk space on C:.
/// </summary>
public static partial class PureCSharpWimSplitter
{
    private const int WimHeaderSize = 208;
    private const int OffsetTableEntrySize = 50;
    private const uint StandardWimVersion = 0x00010D00;

    // WIM Header Flags
    private const uint FLAG_HEADER_READONLY = 0x00000004;
    private const uint FLAG_HEADER_SPANNED = 0x00000008;
    private const uint FLAG_HEADER_RESOURCE_ONLY = 0x00000010;
    private const uint FLAG_HEADER_COMPRESS_LZMS = 0x00080000;

    // Resource Entry Flags (high byte of 8-byte size+flags field)
    private const byte RESHDR_FLAG_FREE = 0x01;
    private const byte RESHDR_FLAG_METADATA = 0x02;
    private const byte RESHDR_FLAG_SOLID = 0x10;

    private static ReadOnlySpan<byte> MswimMagic => "MSWIM\0\0\0"u8;

    [GeneratedRegex(@"<TOTALBYTES>\d+</TOTALBYTES>", RegexOptions.CultureInvariant)]
    private static partial Regex TotalBytesTagRegex();

    private sealed class WimResourceRecord
    {
        public required ulong RawSizeAndFlags { get; init; }
        public required long SizeInWim { get; init; }
        public required byte Flags { get; init; }
        public required long SourceOffset { get; init; }
        public required long OriginalSize { get; init; }
        public required uint RefCount { get; init; }
        public required byte[] Sha1Hash { get; init; }

        public ushort AssignedPartNumber { get; set; }
        public long AssignedOffsetInPart { get; set; }

        public bool IsMetadata => (Flags & RESHDR_FLAG_METADATA) != 0;
    }

    /// <summary>
    /// Checks whether <paramref name="wimStream"/> is a standard non-solid MSWIM v1.13 archive
    /// that can be split directly in pure C# without solid LZMS decompression.
    /// </summary>
    public static bool CanSplitDirectly(Stream wimStream)
    {
        if (!wimStream.CanRead || !wimStream.CanSeek || wimStream.Length < WimHeaderSize)
        {
            return false;
        }

        long savedPos = wimStream.Position;
        try
        {
            wimStream.Position = 0;
            Span<byte> hdr = stackalloc byte[WimHeaderSize];
            if (!TryReadExact(wimStream, hdr))
            {
                return false;
            }

            if (!hdr[..8].SequenceEqual(MswimMagic))
            {
                return false;
            }

            uint cbSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(8, 4));
            uint dwVersion = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(12, 4));
            uint dwFlags = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(16, 4));

            if (cbSize != WimHeaderSize || dwVersion != StandardWimVersion)
            {
                return false;
            }

            // Solid LZMS archives (.esd, version 0x0E00 or LZMS flag with solid blocks) require block re-export
            if ((dwFlags & FLAG_HEADER_COMPRESS_LZMS) != 0)
            {
                return false;
            }

            // Verify offset table is readable and contains no solid entries
            (long otSize, long otOffset, _) = ReadShortResourceHeader(hdr.Slice(48, 24));
            if (otOffset <= 0 || otSize < OffsetTableEntrySize || otOffset + otSize > wimStream.Length)
            {
                return false;
            }

            int checkEntries = (int)Math.Min(64L, otSize / OffsetTableEntrySize);
            byte[] sample = new byte[checkEntries * OffsetTableEntrySize];
            wimStream.Position = otOffset;
            if (!TryReadExact(wimStream, sample))
            {
                return false;
            }

            for (int i = 0; i < checkEntries; i++)
            {
                byte flags = sample[(i * OffsetTableEntrySize) + 7];
                if ((flags & RESHDR_FLAG_SOLID) != 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            wimStream.Position = savedPos;
        }
    }

    public static IReadOnlyList<string> SplitWimFile(
        string sourceWimPath,
        string destinationFirstSwmPath,
        int chunkSizeMb,
        int copyBufferBytes = 2 * 1024 * 1024,
        Action<long, long, int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var src = new FileStream(
            sourceWimPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            Math.Clamp(copyBufferBytes, 64 * 1024, 8 * 1024 * 1024),
            FileOptions.SequentialScan);

        return SplitWimStream(
            src,
            destinationFirstSwmPath,
            chunkSizeMb,
            copyBufferBytes,
            onProgress,
            cancellationToken);
    }

    /// <summary>
    /// Streams a .wim directly from <paramref name="sourceWimStream"/> (e.g. an open ISO entry stream)
    /// into FAT32-safe .swm parts (install.swm, install2.swm, ...) with zero intermediate staging on C:.
    /// </summary>
    public static IReadOnlyList<string> SplitWimStream(
        Stream sourceWimStream,
        string destinationFirstSwmPath,
        int chunkSizeMb,
        int copyBufferBytes = 2 * 1024 * 1024,
        Action<long, long, int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!sourceWimStream.CanRead || !sourceWimStream.CanSeek)
        {
            throw new ArgumentException("Source WIM stream must be readable and seekable.", nameof(sourceWimStream));
        }

        long maxPartBytes = Math.Clamp((long)chunkSizeMb * 1024L * 1024L, 1L * 1024 * 1024, 3950L * 1024 * 1024);

        sourceWimStream.Position = 0;
        byte[] origHeader = new byte[WimHeaderSize];
        if (!TryReadExact(sourceWimStream, origHeader) || !origHeader.AsSpan(0, 8).SequenceEqual(MswimMagic))
        {
            throw new InvalidDataException("Source stream does not begin with a valid MSWIM header.");
        }

        uint cbSize = BinaryPrimitives.ReadUInt32LittleEndian(origHeader.AsSpan(8, 4));
        uint dwVersion = BinaryPrimitives.ReadUInt32LittleEndian(origHeader.AsSpan(12, 4));
        uint origFlags = BinaryPrimitives.ReadUInt32LittleEndian(origHeader.AsSpan(16, 4));
        Guid wimGuid = new(origHeader.AsSpan(24, 16));
        if (wimGuid == Guid.Empty)
        {
            wimGuid = Guid.NewGuid();
        }

        if (cbSize != WimHeaderSize || dwVersion != StandardWimVersion)
        {
            throw new NotSupportedException($"Unsupported WIM header version 0x{dwVersion:X8} (cbSize={cbSize}) for direct pure-C# split.");
        }

        (long otSize, long otOffset, _) = ReadShortResourceHeader(origHeader.AsSpan(48, 24));
        (long xmlSize, long xmlOffset, _) = ReadShortResourceHeader(origHeader.AsSpan(72, 24));

        if (otSize < OffsetTableEntrySize || otOffset <= 0 || otOffset + otSize > sourceWimStream.Length)
        {
            throw new InvalidDataException("Corrupt WIM offset table pointer in header.");
        }

        if (xmlSize <= 0 || xmlOffset <= 0 || xmlOffset + xmlSize > sourceWimStream.Length)
        {
            throw new InvalidDataException("Corrupt WIM XML metadata pointer in header.");
        }

        // 1. Read source Offset Table
        int rawEntryCount = (int)(otSize / OffsetTableEntrySize);
        byte[] rawOtBytes = new byte[rawEntryCount * OffsetTableEntrySize];
        sourceWimStream.Position = otOffset;
        if (!TryReadExact(sourceWimStream, rawOtBytes))
        {
            throw new EndOfStreamException("Unexpected EOF while reading WIM offset table.");
        }

        var metadataEntries = new List<WimResourceRecord>();
        var dataEntries = new List<WimResourceRecord>();

        for (int i = 0; i < rawEntryCount; i++)
        {
            ReadOnlySpan<byte> slot = rawOtBytes.AsSpan(i * OffsetTableEntrySize, OffsetTableEntrySize);
            ulong sf = BinaryPrimitives.ReadUInt64LittleEndian(slot.Slice(0, 8));
            long resSize = (long)(sf & 0x00FFFFFFFFFFFFFFUL);
            byte resFlags = (byte)(sf >> 56);

            if ((resFlags & RESHDR_FLAG_FREE) != 0 || resSize <= 0)
            {
                continue;
            }

            if ((resFlags & RESHDR_FLAG_SOLID) != 0)
            {
                throw new NotSupportedException("Solid WIM/ESD stream requires solid block export.");
            }

            long srcOff = BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(8, 8));
            long origLen = BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(16, 8));
            uint refCnt = BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(26, 4));
            byte[] hash = slot.Slice(30, 20).ToArray();

            var rec = new WimResourceRecord
            {
                RawSizeAndFlags = sf,
                SizeInWim = resSize,
                Flags = resFlags,
                SourceOffset = srcOff,
                OriginalSize = origLen,
                RefCount = refCnt,
                Sha1Hash = hash
            };

            if (rec.IsMetadata)
            {
                metadataEntries.Add(rec);
            }
            else
            {
                dataEntries.Add(rec);
            }
        }

        // Preserve exact Offset Table order for metadataEntries (Image 1..N index mapping in WIMLoadImage!)
        // Sort dataEntries by physical source offset so reading from ISO/disk is smooth forward sequential I/O.
        dataEntries.Sort((a, b) => a.SourceOffset.CompareTo(b.SourceOffset));

        // 2. Read source UTF-16LE XML metadata block
        byte[] origXmlBytes = new byte[xmlSize];
        sourceWimStream.Position = xmlOffset;
        if (!TryReadExact(sourceWimStream, origXmlBytes))
        {
            throw new EndOfStreamException("Unexpected EOF while reading WIM XML metadata.");
        }

        // 3. Plan part assignments (Part 1 holds all <IMAGE> metadata streams + as many data streams as fit)
        long worstCaseFooterBytes = origXmlBytes.Length + ((metadataEntries.Count + dataEntries.Count) * (long)OffsetTableEntrySize) + 65536L;
        var parts = new List<List<WimResourceRecord>>();

        var part1 = new List<WimResourceRecord>();
        long cursor = WimHeaderSize;
        foreach (WimResourceRecord meta in metadataEntries)
        {
            meta.AssignedPartNumber = 1;
            meta.AssignedOffsetInPart = cursor;
            cursor += meta.SizeInWim;
            part1.Add(meta);
        }
        parts.Add(part1);

        int currentPartIdx = 0; // 0-based -> PartNumber = currentPartIdx + 1
        foreach (WimResourceRecord data in dataEntries)
        {
            List<WimResourceRecord> activeList = parts[currentPartIdx];
            long projectedEnd = cursor + data.SizeInWim + Math.Min(worstCaseFooterBytes, origXmlBytes.Length + ((activeList.Count + 1) * (long)OffsetTableEntrySize) + 4096L);

            if (activeList.Count > 0 && projectedEnd > maxPartBytes)
            {
                currentPartIdx++;
                activeList = new List<WimResourceRecord>();
                parts.Add(activeList);
                cursor = WimHeaderSize;
            }

            data.AssignedPartNumber = (ushort)(currentPartIdx + 1);
            data.AssignedOffsetInPart = cursor;
            cursor += data.SizeInWim;
            activeList.Add(data);
        }

        ushort totalParts = (ushort)parts.Count;
        long totalPayloadBytes = metadataEntries.Sum(m => m.SizeInWim) + dataEntries.Sum(d => d.SizeInWim);
        long copiedPayloadBytes = 0;

        string outDir = Path.GetDirectoryName(destinationFirstSwmPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);
        string baseName = Path.GetFileNameWithoutExtension(destinationFirstSwmPath);
        string ext = Path.GetExtension(destinationFirstSwmPath);
        if (string.IsNullOrEmpty(ext))
        {
            ext = ".swm";
        }

        int safeBufLen = Math.Clamp(copyBufferBytes, 64 * 1024, 8 * 1024 * 1024);
        byte[] copyBuffer = new byte[safeBufLen];
        var createdPartPaths = new List<string>(totalParts);

        // 4. Stream each part cleanly to target partition
        for (int pIdx = 0; pIdx < totalParts; pIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ushort partNum = (ushort)(pIdx + 1);
            string partPath = partNum == 1
                ? Path.Combine(outDir, $"{baseName}{ext}")
                : Path.Combine(outDir, $"{baseName}{partNum}{ext}");

            createdPartPaths.Add(partPath);
            List<WimResourceRecord> partRecords = parts[pIdx];

            using var outFs = new FileStream(
                partPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                safeBufLen,
                FileOptions.SequentialScan);

            // Reserve 208-byte WIM header slot
            outFs.Write(new byte[WimHeaderSize], 0, WimHeaderSize);

            foreach (WimResourceRecord rec in partRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (outFs.Position != rec.AssignedOffsetInPart)
                {
                    throw new InvalidOperationException($"Internal SWM offset mismatch at part {partNum}: expected {rec.AssignedOffsetInPart}, actual {outFs.Position}.");
                }

                sourceWimStream.Position = rec.SourceOffset;
                long remaining = rec.SizeInWim;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(copyBuffer.Length, remaining);
                    int read = sourceWimStream.Read(copyBuffer, 0, toRead);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException($"Unexpected EOF in source WIM at offset {sourceWimStream.Position} (needed {remaining} more bytes).");
                    }

                    outFs.Write(copyBuffer, 0, read);
                    remaining -= read;
                    copiedPayloadBytes += read;
                }

                onProgress?.Invoke(copiedPayloadBytes, Math.Max(1L, totalPayloadBytes), partNum, totalParts);
            }

            // Write this part's Offset Table
            long partOtOffset = outFs.Position;
            byte[] partOtBytes = SerializeOffsetTable(partRecords);
            outFs.Write(partOtBytes, 0, partOtBytes.Length);

            // Write this part's UTF-16LE XML metadata (updating <TOTALBYTES> to exact part length)
            long partXmlOffset = outFs.Position;
            byte[] partXmlBytes = BuildPartXmlBytesWithExactTotalBytes(origXmlBytes, partXmlOffset);
            outFs.Write(partXmlBytes, 0, partXmlBytes.Length);

            // Build & write finalized 208-byte WIMHEADER_V1_PACKED at offset 0
            byte[] partHeader = BuildPartHeader(
                origHeader,
                origFlags,
                wimGuid,
                partNum,
                totalParts,
                hasMetadata: partRecords.Any(r => r.IsMetadata),
                partOtOffset: partOtOffset,
                partOtSize: partOtBytes.Length,
                partXmlOffset: partXmlOffset,
                partXmlSize: partXmlBytes.Length);

            outFs.Position = 0;
            outFs.Write(partHeader, 0, WimHeaderSize);
            outFs.Flush(flushToDisk: true);
        }

        return createdPartPaths;
    }

    private static byte[] SerializeOffsetTable(IReadOnlyList<WimResourceRecord> records)
    {
        byte[] buf = new byte[records.Count * OffsetTableEntrySize];
        for (int i = 0; i < records.Count; i++)
        {
            WimResourceRecord r = records[i];
            Span<byte> slot = buf.AsSpan(i * OffsetTableEntrySize, OffsetTableEntrySize);
            BinaryPrimitives.WriteUInt64LittleEndian(slot.Slice(0, 8), r.RawSizeAndFlags);
            BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(8, 8), r.AssignedOffsetInPart);
            BinaryPrimitives.WriteInt64LittleEndian(slot.Slice(16, 8), r.OriginalSize);
            BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(24, 2), r.AssignedPartNumber);
            BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(26, 4), r.RefCount);
            r.Sha1Hash.AsSpan(0, 20).CopyTo(slot.Slice(30, 20));
        }

        return buf;
    }

    private static byte[] BuildPartHeader(
        byte[] origHeader,
        uint origFlags,
        Guid wimGuid,
        ushort partNum,
        ushort totalParts,
        bool hasMetadata,
        long partOtOffset,
        long partOtSize,
        long partXmlOffset,
        long partXmlSize)
    {
        byte[] hdr = new byte[WimHeaderSize];
        Buffer.BlockCopy(origHeader, 0, hdr, 0, WimHeaderSize);

        // Clear rhBootMetadata [96..119], dwBootIndex [120..123], rhIntegrity [124..147], bUnused [148..207]
        Array.Clear(hdr, 96, WimHeaderSize - 96);

        uint flags = origFlags | FLAG_HEADER_READONLY;
        if (totalParts > 1)
        {
            flags |= FLAG_HEADER_SPANNED;
        }
        if (!hasMetadata)
        {
            flags |= FLAG_HEADER_RESOURCE_ONLY;
        }
        else
        {
            flags &= ~FLAG_HEADER_RESOURCE_ONLY;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(16, 4), flags);
        wimGuid.TryWriteBytes(hdr.AsSpan(24, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(40, 2), partNum);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(42, 2), totalParts);

        WriteShortResourceHeader(hdr.AsSpan(48, 24), partOtSize, RESHDR_FLAG_METADATA, partOtOffset, partOtSize);
        WriteShortResourceHeader(hdr.AsSpan(72, 24), partXmlSize, RESHDR_FLAG_METADATA, partXmlOffset, partXmlSize);

        return hdr;
    }

    private static byte[] BuildPartXmlBytesWithExactTotalBytes(byte[] origXmlBytes, long xmlStartOffset)
    {
        bool hasBom = origXmlBytes.Length >= 2 && origXmlBytes[0] == 0xFF && origXmlBytes[1] == 0xFE;
        int start = hasBom ? 2 : 0;
        string xmlStr = Encoding.Unicode.GetString(origXmlBytes, start, origXmlBytes.Length - start);

        if (!TotalBytesTagRegex().IsMatch(xmlStr))
        {
            return origXmlBytes;
        }

        // Converge exact total file length in 2 iterations
        long estTotal = xmlStartOffset + origXmlBytes.Length;
        byte[] encoded = origXmlBytes;
        for (int iter = 0; iter < 3; iter++)
        {
            string updated = TotalBytesTagRegex().Replace(xmlStr, $"<TOTALBYTES>{estTotal}</TOTALBYTES>", count: 1);
            byte[] body = Encoding.Unicode.GetBytes(updated);
            encoded = hasBom ? new byte[2 + body.Length] : body;
            if (hasBom)
            {
                encoded[0] = 0xFF;
                encoded[1] = 0xFE;
                Buffer.BlockCopy(body, 0, encoded, 2, body.Length);
            }

            long actualTotal = xmlStartOffset + encoded.Length;
            if (actualTotal == estTotal)
            {
                break;
            }
            estTotal = actualTotal;
        }

        return encoded;
    }

    private static (long Size, long Offset, long OriginalSize) ReadShortResourceHeader(ReadOnlySpan<byte> span24)
    {
        ulong sf = BinaryPrimitives.ReadUInt64LittleEndian(span24.Slice(0, 8));
        long size = (long)(sf & 0x00FFFFFFFFFFFFFFUL);
        long offset = BinaryPrimitives.ReadInt64LittleEndian(span24.Slice(8, 8));
        long origSize = BinaryPrimitives.ReadInt64LittleEndian(span24.Slice(16, 8));
        return (size, offset, origSize);
    }

    private static void WriteShortResourceHeader(Span<byte> dest24, long size, byte flags, long offset, long originalSize)
    {
        ulong sf = ((ulong)size & 0x00FFFFFFFFFFFFFFUL) | ((ulong)flags << 56);
        BinaryPrimitives.WriteUInt64LittleEndian(dest24.Slice(0, 8), sf);
        BinaryPrimitives.WriteInt64LittleEndian(dest24.Slice(8, 8), offset);
        BinaryPrimitives.WriteInt64LittleEndian(dest24.Slice(16, 8), originalSize);
    }

    private static bool TryReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n <= 0)
            {
                return false;
            }
            total += n;
        }
        return true;
    }
}
