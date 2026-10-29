using System.Buffers.Binary;
using System.Text;

namespace UsbFreeInstaller.Core.Services;

public readonly record struct IsoByteExtent(long FileOffsetBytes, long LengthBytes, bool IsZeroSparse = false);

public sealed class IsoFileEntry
{
    public required string RelativePath { get; init; }
    public required long SizeBytes { get; init; }
    public IReadOnlyList<IsoByteExtent> Extents { get; init; } = Array.Empty<IsoByteExtent>();
    public byte[]? InlineData { get; init; }
}

/// <summary>
/// Pure C# zero-mount ISO-9660 / Joliet (Levels 1–3 incl. multi-extent &gt;4 GB) and UDF (1.02–2.50 incl. Metadata Partitions)
/// reader and stream extractor. Allows SWBI to inspect and extract ANY official or custom Windows 10/11 ISO directly
/// from disk without relying on PowerShell Mount-DiskImage, Virtual Disk Service, AutoMount, or WMI MSFT_Volume.
/// </summary>
public sealed class IsoImageStreamReader : IDisposable
{
    public const int SectorSize = 2048;

    private readonly FileStream _isoStream;
    private readonly Dictionary<string, IsoFileEntry> _entriesByPath;

    public string IsoFilePath { get; }

    public IReadOnlyCollection<IsoFileEntry> Files => _entriesByPath.Values;

    private IsoImageStreamReader(
        string isoFilePath,
        FileStream isoStream,
        Dictionary<string, IsoFileEntry> entriesByPath)
    {
        IsoFilePath = isoFilePath;
        _isoStream = isoStream;
        _entriesByPath = entriesByPath;
    }

    public static IsoImageStreamReader Open(string isoFilePath)
    {
        var fs = new FileStream(
            isoFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess);

        try
        {
            Dictionary<string, IsoFileEntry>? udfEntries = TryParseUdf(fs);
            Dictionary<string, IsoFileEntry>? isoEntries = TryParseIso9660(fs);

            Dictionary<string, IsoFileEntry>? chosen = SelectBestCatalog(udfEntries, isoEntries);
            if (chosen is null || chosen.Count == 0)
            {
                throw new InvalidDataException("File is not a readable UDF or ISO-9660/Joliet disc image.");
            }

            return new IsoImageStreamReader(isoFilePath, fs, chosen);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public bool FileExists(string relativePath) =>
        TryGetFile(relativePath, out _);

    public bool TryGetFile(string relativePath, out IsoFileEntry? entry)
    {
        string key = NormalizeRelativePath(relativePath);
        return _entriesByPath.TryGetValue(key, out entry);
    }

    public Stream OpenFileStream(IsoFileEntry entry)
    {
        if (entry.InlineData is not null)
        {
            return new MemoryStream(entry.InlineData, writable: false);
        }

        return new IsoExtentSliceStream(_isoStream, entry.Extents, entry.SizeBytes);
    }

    public Stream OpenFileStream(string relativePath)
    {
        if (!TryGetFile(relativePath, out IsoFileEntry? entry) || entry is null)
        {
            throw new FileNotFoundException($"File '{relativePath}' was not found inside ISO '{IsoFilePath}'.");
        }

        return OpenFileStream(entry);
    }

    public async Task ExtractFileAsync(
        IsoFileEntry entry,
        string destinationPath,
        byte[] buffer,
        Action<int>? onChunkCopied = null,
        CancellationToken cancellationToken = default)
    {
        // Defence in depth: callers already containment-check the destination, but a traversal segment
        // reaching this writer would silently overwrite files outside the installer partition.
        if (SafeRelativePath.ContainsTraversalSegment(destinationPath))
        {
            throw new InvalidOperationException(
                $"Refusing to extract '{entry.RelativePath}': the destination path contains a traversal segment.");
        }

        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(destinationPath))
        {
            File.SetAttributes(destinationPath, FileAttributes.Normal);
        }

        await using Stream src = OpenFileStream(entry);
        await using var dst = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous);

        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            onChunkCopied?.Invoke(read);
        }

        File.SetAttributes(destinationPath, FileAttributes.Normal);
    }

    public void Dispose() => _isoStream.Dispose();

    public static string NormalizeRelativePath(string path) =>
        path.Trim().TrimStart('\\', '/').Replace('/', '\\');

    private static Dictionary<string, IsoFileEntry>? SelectBestCatalog(
        Dictionary<string, IsoFileEntry>? udf,
        Dictionary<string, IsoFileEntry>? iso)
    {
        if (udf is { Count: > 0 } && iso is { Count: > 0 })
        {
            // Bridge ISOs have only a 1-file README.TXT stub in ISO9660 and full tree in UDF;
            // conversely some custom ISOs master full files in Joliet. Merge missing entries into the richer catalog.
            bool udfHasBootWim = udf.Keys.Any(k => k.EndsWith(@"sources\boot.wim", StringComparison.OrdinalIgnoreCase));
            bool isoHasBootWim = iso.Keys.Any(k => k.EndsWith(@"sources\boot.wim", StringComparison.OrdinalIgnoreCase));

            Dictionary<string, IsoFileEntry> primary = (udfHasBootWim || (!isoHasBootWim && udf.Count >= iso.Count)) ? udf : iso;
            Dictionary<string, IsoFileEntry> secondary = ReferenceEquals(primary, udf) ? iso : udf;

            foreach ((string key, IsoFileEntry val) in secondary)
            {
                if (!primary.ContainsKey(key))
                {
                    primary[key] = val;
                }
                else if (val.SizeBytes > primary[key].SizeBytes)
                {
                    // Prefer UDF entry over truncated ISO9660 entry for >4GB files
                    primary[key] = val;
                }
            }

            return primary;
        }

        return udf is { Count: > 0 } ? udf : iso;
    }

    // =========================================================================
    // 1. UDF (ECMA-167 / UDF 1.02, 1.50, 2.01, 2.50) PARSER
    // =========================================================================

    private sealed class UdfContext
    {
        public required FileStream Stream { get; init; }
        public int BlockSize { get; set; } = SectorSize;
        public Dictionary<ushort, long> PhysicalPartitionStartSector { get; } = new();
        public List<Func<uint, long>> PartitionReferenceResolvers { get; } = new();
    }

    private static Dictionary<string, IsoFileEntry>? TryParseUdf(FileStream fs)
    {
        try
        {
            long totalSectors = fs.Length / SectorSize;
            if (totalSectors < 260)
            {
                return null;
            }

            long[] anchorCandidates = { 256L, totalSectors - 257L, totalSectors - 1L };
            byte[] sectorBuf = new byte[SectorSize];
            uint mvdsLenBytes = 0;
            uint mvdsLocSector = 0;

            foreach (long candidate in anchorCandidates)
            {
                if (candidate <= 0 || candidate >= totalSectors)
                {
                    continue;
                }

                ReadSector(fs, candidate, sectorBuf);
                ushort tagId = BinaryPrimitives.ReadUInt16LittleEndian(sectorBuf.AsSpan(0, 2));
                if (tagId == 2) // Anchor Volume Descriptor Pointer (AVDP)
                {
                    mvdsLenBytes = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(16, 4));
                    mvdsLocSector = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(20, 4));
                    if (mvdsLenBytes >= SectorSize && mvdsLocSector > 0)
                    {
                        break;
                    }
                }
            }

            if (mvdsLenBytes < SectorSize || mvdsLocSector == 0)
            {
                return null;
            }

            var ctx = new UdfContext { Stream = fs };
            int mvdsSectors = (int)Math.Min(128, mvdsLenBytes / SectorSize);

            uint fsdLba = 0;
            ushort fsdPartRef = 0;
            byte[]? rawPartMaps = null;
            uint numPartMaps = 0;

            for (int i = 0; i < mvdsSectors; i++)
            {
                ReadSector(fs, mvdsLocSector + i, sectorBuf);
                ushort tagId = BinaryPrimitives.ReadUInt16LittleEndian(sectorBuf.AsSpan(0, 2));
                if (tagId == 8) // Terminating Descriptor
                {
                    break;
                }

                if (tagId == 5) // Partition Descriptor
                {
                    ushort partNum = BinaryPrimitives.ReadUInt16LittleEndian(sectorBuf.AsSpan(22, 2));
                    uint startLba = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(188, 4));
                    ctx.PhysicalPartitionStartSector[partNum] = startLba;
                }
                else if (tagId == 6) // Logical Volume Descriptor
                {
                    uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(212, 4));
                    if (blockSize >= 512 && blockSize <= 65536)
                    {
                        ctx.BlockSize = (int)blockSize;
                    }

                    // FileSetDescriptor long_ad at offset 248: [0..3]=length, [4..7]=lba, [8..9]=partRef
                    fsdLba = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(252, 4));
                    fsdPartRef = BinaryPrimitives.ReadUInt16LittleEndian(sectorBuf.AsSpan(256, 2));

                    uint mapTableLen = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(264, 4));
                    numPartMaps = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(268, 4));
                    if (mapTableLen > 0 && 440 + mapTableLen <= sectorBuf.Length)
                    {
                        rawPartMaps = sectorBuf.AsSpan(440, (int)mapTableLen).ToArray();
                    }
                }
            }

            if (ctx.PhysicalPartitionStartSector.Count == 0)
            {
                return null;
            }

            BuildUdfPartitionResolvers(ctx, rawPartMaps, numPartMaps);

            long fsdOffset = ResolvePartBlockOffset(ctx, fsdPartRef, fsdLba);
            byte[] blockBuf = new byte[ctx.BlockSize];
            ReadAt(fs, fsdOffset, blockBuf);

            ushort fsdTag = BinaryPrimitives.ReadUInt16LittleEndian(blockBuf.AsSpan(0, 2));
            if (fsdTag != 256) // File Set Descriptor
            {
                return null;
            }

            // Root Directory ICB long_ad at offset 400
            uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(blockBuf.AsSpan(404, 4));
            ushort rootPartRef = BinaryPrimitives.ReadUInt16LittleEndian(blockBuf.AsSpan(408, 2));

            var results = new Dictionary<string, IsoFileEntry>(StringComparer.OrdinalIgnoreCase);
            var visitedDirs = new HashSet<long>();
            WalkUdfDirectory(ctx, rootPartRef, rootLba, string.Empty, results, visitedDirs, depth: 0);
            return results.Count > 0 ? results : null;
        }
        catch
        {
            return null;
        }
    }

    private static void BuildUdfPartitionResolvers(UdfContext ctx, byte[]? rawMaps, uint numMaps)
    {
        long defaultPhysStart = ctx.PhysicalPartitionStartSector.Values.First();

        if (rawMaps is null || numMaps == 0)
        {
            ctx.PartitionReferenceResolvers.Add(lba => (defaultPhysStart + lba) * ctx.BlockSize);
            return;
        }

        int pos = 0;
        for (int i = 0; i < numMaps && pos + 2 <= rawMaps.Length; i++)
        {
            byte mapType = rawMaps[pos];
            byte mapLen = rawMaps[pos + 1];
            if (mapLen == 0 || pos + mapLen > rawMaps.Length)
            {
                break;
            }

            ReadOnlySpan<byte> mapSpan = rawMaps.AsSpan(pos, mapLen);
            if (mapType == 1 && mapLen >= 6)
            {
                ushort partNum = BinaryPrimitives.ReadUInt16LittleEndian(mapSpan.Slice(4, 2));
                long physStart = ctx.PhysicalPartitionStartSector.GetValueOrDefault(partNum, defaultPhysStart);
                ctx.PartitionReferenceResolvers.Add(lba => (physStart + lba) * ctx.BlockSize);
            }
            else if (mapType == 2 && mapLen >= 64)
            {
                string ident = Encoding.ASCII.GetString(mapSpan.Slice(5, 23)).TrimEnd('\0', ' ');
                ushort hostPartNum = BinaryPrimitives.ReadUInt16LittleEndian(mapSpan.Slice(38, 2));
                long physStart = ctx.PhysicalPartitionStartSector.GetValueOrDefault(hostPartNum, defaultPhysStart);

                if (ident.StartsWith("*UDF Metadata Partition", StringComparison.OrdinalIgnoreCase))
                {
                    uint metaFileLoc = BinaryPrimitives.ReadUInt32LittleEndian(mapSpan.Slice(40, 4));
                    uint allocUnitBlocks = BinaryPrimitives.ReadUInt32LittleEndian(mapSpan.Slice(48, 4));
                    if (allocUnitBlocks == 0) allocUnitBlocks = 1;

                    // Temporarily register physical resolver for hostPartNum so we can read the Metadata File ICB
                    long metaIcbOffset = (physStart + metaFileLoc) * ctx.BlockSize;
                    IReadOnlyList<IsoByteExtent> metaExtents = ReadUdfIcbExtents(ctx, 0, metaIcbOffset, physStart, out _, out _);

                    ctx.PartitionReferenceResolvers.Add(metaBlockLba =>
                    {
                        long byteInMeta = (long)metaBlockLba * ctx.BlockSize;
                        return MapLogicalByteThroughExtents(metaExtents, byteInMeta);
                    });
                }
                else
                {
                    ctx.PartitionReferenceResolvers.Add(lba => (physStart + lba) * ctx.BlockSize);
                }
            }
            else
            {
                ctx.PartitionReferenceResolvers.Add(lba => (defaultPhysStart + lba) * ctx.BlockSize);
            }

            pos += mapLen;
        }

        if (ctx.PartitionReferenceResolvers.Count == 0)
        {
            ctx.PartitionReferenceResolvers.Add(lba => (defaultPhysStart + lba) * ctx.BlockSize);
        }
    }

    private static long ResolvePartBlockOffset(UdfContext ctx, ushort partRef, uint lba)
    {
        if (partRef < ctx.PartitionReferenceResolvers.Count)
        {
            return ctx.PartitionReferenceResolvers[partRef](lba);
        }

        return ctx.PartitionReferenceResolvers[0](lba);
    }

    private static long MapLogicalByteThroughExtents(IReadOnlyList<IsoByteExtent> extents, long logicalByteOffset)
    {
        long cur = 0;
        foreach (IsoByteExtent ext in extents)
        {
            if (logicalByteOffset >= cur && logicalByteOffset < cur + ext.LengthBytes)
            {
                return ext.FileOffsetBytes + (logicalByteOffset - cur);
            }

            cur += ext.LengthBytes;
        }

        return extents.Count > 0 ? extents[0].FileOffsetBytes + logicalByteOffset : logicalByteOffset;
    }

    private static void WalkUdfDirectory(
        UdfContext ctx,
        ushort dirPartRef,
        uint dirLba,
        string parentRelPath,
        Dictionary<string, IsoFileEntry> results,
        HashSet<long> visitedDirs,
        int depth)
    {
        if (depth > 32)
        {
            return;
        }

        long icbByteOffset = ResolvePartBlockOffset(ctx, dirPartRef, dirLba);
        if (!visitedDirs.Add(icbByteOffset))
        {
            return;
        }

        IReadOnlyList<IsoByteExtent> dirExtents = ReadUdfIcbExtents(ctx, dirPartRef, icbByteOffset, null, out long dirSize, out byte[]? inlineDirBytes);
        byte[] dirData;
        if (inlineDirBytes is not null)
        {
            dirData = inlineDirBytes;
        }
        else
        {
            if (dirSize <= 0 || dirSize > 64 * 1024 * 1024)
            {
                return;
            }

            dirData = new byte[dirSize];
            using var s = new IsoExtentSliceStream(ctx.Stream, dirExtents, dirSize);
            ReadStreamFully(s, dirData);
        }

        int pos = 0;
        while (pos + 38 <= dirData.Length)
        {
            ushort tagId = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(pos, 2));
            if (tagId != 257) // File Identifier Descriptor (FID)
            {
                // Advance to next block boundary if padding encountered inside directory stream
                int nextBlock = ((pos / ctx.BlockSize) + 1) * ctx.BlockSize;
                if (nextBlock <= pos || nextBlock >= dirData.Length)
                {
                    break;
                }

                pos = nextBlock;
                continue;
            }

            byte characteristics = dirData[pos + 18];
            byte nameByteLen = dirData[pos + 19];
            uint childIcbLba = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos + 24, 4));
            ushort childIcbPartRef = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(pos + 28, 2));
            ushort implUseLen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(pos + 36, 2));

            int fidTotalUnpadded = 38 + implUseLen + nameByteLen;
            int fidTotalPadded = (fidTotalUnpadded + 3) & ~3;
            if (fidTotalPadded <= 0 || pos + fidTotalUnpadded > dirData.Length)
            {
                break;
            }

            bool isDeleted = (characteristics & 0x04) != 0;
            bool isParent = (characteristics & 0x08) != 0;
            bool isDirectory = (characteristics & 0x02) != 0;

            if (!isDeleted && !isParent && nameByteLen > 0)
            {
                string entryName = DecodeOstaCompressedUnicode(dirData.AsSpan(pos + 38 + implUseLen, nameByteLen));
                if (!string.IsNullOrWhiteSpace(entryName) && entryName is not "." and not "..")
                {
                    string relPath = string.IsNullOrEmpty(parentRelPath)
                        ? entryName
                        : $"{parentRelPath}\\{entryName}";

                    if (isDirectory)
                    {
                        WalkUdfDirectory(ctx, childIcbPartRef, childIcbLba, relPath, results, visitedDirs, depth + 1);
                    }
                    else
                    {
                        long fileIcbOffset = ResolvePartBlockOffset(ctx, childIcbPartRef, childIcbLba);
                        IReadOnlyList<IsoByteExtent> fileExtents = ReadUdfIcbExtents(ctx, childIcbPartRef, fileIcbOffset, null, out long fileLen, out byte[]? inlineFileBytes);
                        string norm = NormalizeRelativePath(relPath);
                        results[norm] = new IsoFileEntry
                        {
                            RelativePath = norm,
                            SizeBytes = fileLen,
                            Extents = fileExtents,
                            InlineData = inlineFileBytes
                        };
                    }
                }
            }

            pos += fidTotalPadded;
        }
    }

    private static IReadOnlyList<IsoByteExtent> ReadUdfIcbExtents(
        UdfContext ctx,
        ushort ownerPartRef,
        long icbByteOffset,
        long? fixedPhysicalPartStartSector,
        out long infoLength,
        out byte[]? inlineBytes)
    {
        infoLength = 0;
        inlineBytes = null;

        byte[] block = new byte[ctx.BlockSize];
        ReadAt(ctx.Stream, icbByteOffset, block);

        ushort tagId = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(0, 2));
        if (tagId is not 261 and not 266) // File Entry (261) or Extended File Entry (266)
        {
            return Array.Empty<IsoByteExtent>();
        }

        ushort icbFlags = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(34, 2));
        int adType = icbFlags & 0x07;
        infoLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(56, 8));

        uint eaLen = tagId == 261
            ? BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(168, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(208, 4));
        uint adLen = tagId == 261
            ? BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(172, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(212, 4));
        int adStart = (tagId == 261 ? 176 : 216) + (int)eaLen;

        if (adStart < 0 || adStart + adLen > block.Length)
        {
            return Array.Empty<IsoByteExtent>();
        }

        if (adType == 3) // Embedded inline file data directly inside ICB
        {
            int copyLen = (int)Math.Min(infoLength, adLen);
            inlineBytes = block.AsSpan(adStart, copyLen).ToArray();
            return Array.Empty<IsoByteExtent>();
        }

        var extents = new List<IsoByteExtent>();
        ParseUdfAllocationDescriptors(
            ctx,
            block.AsSpan(adStart, (int)adLen),
            adType,
            ownerPartRef,
            fixedPhysicalPartStartSector,
            extents,
            continuationDepth: 0);

        return extents;
    }

    private static void ParseUdfAllocationDescriptors(
        UdfContext ctx,
        ReadOnlySpan<byte> adBytes,
        int adType,
        ushort ownerPartRef,
        long? fixedPhysicalPartStartSector,
        List<IsoByteExtent> extents,
        int continuationDepth)
    {
        if (continuationDepth > 64)
        {
            return;
        }

        int stride = adType switch
        {
            0 => 8,  // short_ad
            1 => 16, // long_ad
            2 => 20, // ext_ad
            _ => 0
        };

        if (stride == 0)
        {
            return;
        }

        for (int pos = 0; pos + stride <= adBytes.Length; pos += stride)
        {
            uint rawLenAndType = BinaryPrimitives.ReadUInt32LittleEndian(adBytes.Slice(pos, 4));
            uint extType = rawLenAndType >> 30;
            long byteLen = rawLenAndType & 0x3FFF_FFFFU;
            if (byteLen == 0)
            {
                break;
            }

            uint lba;
            ushort partRef = ownerPartRef;
            if (adType == 0)
            {
                lba = BinaryPrimitives.ReadUInt32LittleEndian(adBytes.Slice(pos + 4, 4));
            }
            else if (adType == 1)
            {
                lba = BinaryPrimitives.ReadUInt32LittleEndian(adBytes.Slice(pos + 4, 4));
                partRef = BinaryPrimitives.ReadUInt16LittleEndian(adBytes.Slice(pos + 8, 2));
            }
            else
            {
                lba = BinaryPrimitives.ReadUInt32LittleEndian(adBytes.Slice(pos + 12, 4));
                partRef = BinaryPrimitives.ReadUInt16LittleEndian(adBytes.Slice(pos + 16, 2));
            }

            long diskOffset = fixedPhysicalPartStartSector.HasValue
                ? (fixedPhysicalPartStartSector.Value + lba) * ctx.BlockSize
                : ResolvePartBlockOffset(ctx, partRef, lba);

            if (extType == 3) // Next extent of allocation descriptors (Tag 258 AED)
            {
                byte[] nextAdBuf = new byte[byteLen];
                ReadAt(ctx.Stream, diskOffset, nextAdBuf);
                ushort aedTag = BinaryPrimitives.ReadUInt16LittleEndian(nextAdBuf.AsSpan(0, 2));
                if (aedTag == 258 && nextAdBuf.Length >= 24)
                {
                    uint nextLen = BinaryPrimitives.ReadUInt32LittleEndian(nextAdBuf.AsSpan(20, 4));
                    if (24 + nextLen <= nextAdBuf.Length)
                    {
                        ParseUdfAllocationDescriptors(
                            ctx,
                            nextAdBuf.AsSpan(24, (int)nextLen),
                            adType,
                            ownerPartRef,
                            fixedPhysicalPartStartSector,
                            extents,
                            continuationDepth + 1);
                    }
                }
            }
            else if (extType == 0) // Recorded & allocated
            {
                extents.Add(new IsoByteExtent(diskOffset, byteLen, IsZeroSparse: false));
            }
            else // Unrecorded / sparse zero hole
            {
                extents.Add(new IsoByteExtent(0, byteLen, IsZeroSparse: true));
            }
        }
    }

    private static string DecodeOstaCompressedUnicode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= 1)
        {
            return string.Empty;
        }

        byte compId = bytes[0];
        ReadOnlySpan<byte> payload = bytes[1..];
        return compId switch
        {
            8 => Encoding.Latin1.GetString(payload).TrimEnd('\0'),
            16 => Encoding.BigEndianUnicode.GetString(payload.Slice(0, payload.Length & ~1)).TrimEnd('\0'),
            _ => Encoding.UTF8.GetString(bytes).TrimEnd('\0')
        };
    }

    // =========================================================================
    // 2. ISO-9660 + JOLIET (LEVELS 1–3 INCL. MULTI-EXTENT >4 GB) PARSER
    // =========================================================================

    private static Dictionary<string, IsoFileEntry>? TryParseIso9660(FileStream fs)
    {
        try
        {
            if (fs.Length < 17L * SectorSize)
            {
                return null;
            }

            byte[] sector = new byte[SectorSize];
            byte[]? pvdRootRecord = null;
            byte[]? jolietRootRecord = null;

            for (long sec = 16; sec < 64 && (sec + 1) * SectorSize <= fs.Length; sec++)
            {
                ReadSector(fs, sec, sector);
                if (sector[1] != (byte)'C' || sector[2] != (byte)'D' || sector[3] != (byte)'0' || sector[4] != (byte)'0' || sector[5] != (byte)'1')
                {
                    break;
                }

                byte vdType = sector[0];
                if (vdType == 255) // Volume Descriptor Set Terminator
                {
                    break;
                }

                if (vdType == 1 && pvdRootRecord is null)
                {
                    pvdRootRecord = sector.AsSpan(156, 34).ToArray();
                }
                else if (vdType == 2)
                {
                    // Check UCS-2 Joliet escape sequences: 25 2F 40, 25 2F 43, or 25 2F 45
                    if (sector[88] == 0x25 && sector[89] == 0x2F && (sector[90] is 0x40 or 0x43 or 0x45))
                    {
                        jolietRootRecord = sector.AsSpan(156, 34).ToArray();
                    }
                }
            }

            byte[]? rootRec = jolietRootRecord ?? pvdRootRecord;
            if (rootRec is null)
            {
                return null;
            }

            bool isJoliet = jolietRootRecord is not null;
            uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(rootRec.AsSpan(2, 4));
            uint rootLen = BinaryPrimitives.ReadUInt32LittleEndian(rootRec.AsSpan(10, 4));

            var results = new Dictionary<string, IsoFileEntry>(StringComparer.OrdinalIgnoreCase);
            var visitedLbas = new HashSet<uint>();
            WalkIso9660Directory(fs, rootLba, rootLen, isJoliet, string.Empty, results, visitedLbas, depth: 0);
            return results.Count > 0 ? results : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WalkIso9660Directory(
        FileStream fs,
        uint dirLba,
        uint dirByteLength,
        bool isJoliet,
        string parentRelPath,
        Dictionary<string, IsoFileEntry> results,
        HashSet<uint> visitedLbas,
        int depth)
    {
        if (depth > 32 || dirLba == 0 || dirByteLength == 0 || dirByteLength > 32 * 1024 * 1024 || !visitedLbas.Add(dirLba))
        {
            return;
        }

        long dirDiskOffset = (long)dirLba * SectorSize;
        if (dirDiskOffset + dirByteLength > fs.Length)
        {
            return;
        }

        byte[] dirBytes = new byte[dirByteLength];
        ReadAt(fs, dirDiskOffset, dirBytes);

        // Multi-extent accumulator for ISO-9660 Level 3 files > 4 GB
        string? pendingMultiName = null;
        var pendingExtents = new List<IsoByteExtent>();

        int pos = 0;
        while (pos < dirBytes.Length)
        {
            byte recLen = dirBytes[pos];
            if (recLen == 0)
            {
                // Directory records do not cross 2048-byte sector boundaries; jump to next sector
                int nextSector = ((pos / SectorSize) + 1) * SectorSize;
                if (nextSector <= pos || nextSector >= dirBytes.Length)
                {
                    break;
                }

                pos = nextSector;
                continue;
            }

            if (recLen < 34 || pos + recLen > dirBytes.Length)
            {
                break;
            }

            ReadOnlySpan<byte> rec = dirBytes.AsSpan(pos, recLen);
            uint extentLba = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(2, 4));
            uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(10, 4));
            byte flags = rec[25];
            byte nameLen = rec[32];

            bool isDirectory = (flags & 0x02) != 0;
            bool hasMoreExtents = (flags & 0x80) != 0;

            if (nameLen > 0 && 33 + nameLen <= recLen)
            {
                ReadOnlySpan<byte> rawName = rec.Slice(33, nameLen);
                bool isSelfOrParent = nameLen == 1 && (rawName[0] == 0x00 || rawName[0] == 0x01);

                if (!isSelfOrParent)
                {
                    string decoded = isJoliet
                        ? Encoding.BigEndianUnicode.GetString(rawName.Slice(0, rawName.Length & ~1))
                        : Encoding.ASCII.GetString(rawName);

                    int semi = decoded.IndexOf(';');
                    if (semi >= 0)
                    {
                        decoded = decoded[..semi];
                    }

                    // ISO-9660 appends trailing '.' for extensionless files like 'README.'
                    if (!isDirectory && decoded.EndsWith('.'))
                    {
                        decoded = decoded.TrimEnd('.');
                    }

                    decoded = decoded.Trim();
                    if (!string.IsNullOrEmpty(decoded))
                    {
                        string relPath = string.IsNullOrEmpty(parentRelPath)
                            ? decoded
                            : $"{parentRelPath}\\{decoded}";

                        if (isDirectory)
                        {
                            WalkIso9660Directory(fs, extentLba, dataLen, isJoliet, relPath, results, visitedLbas, depth + 1);
                        }
                        else
                        {
                            var ext = new IsoByteExtent((long)extentLba * SectorSize, dataLen);
                            if (hasMoreExtents)
                            {
                                if (!string.Equals(pendingMultiName, relPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    pendingExtents.Clear();
                                    pendingMultiName = relPath;
                                }

                                pendingExtents.Add(ext);
                            }
                            else if (pendingMultiName is not null &&
                                     string.Equals(pendingMultiName, relPath, StringComparison.OrdinalIgnoreCase))
                            {
                                pendingExtents.Add(ext);
                                IReadOnlyList<IsoByteExtent> finalized = pendingExtents.ToArray();
                                long totalSize = finalized.Sum(e => e.LengthBytes);
                                string norm = NormalizeRelativePath(relPath);
                                results[norm] = new IsoFileEntry
                                {
                                    RelativePath = norm,
                                    SizeBytes = totalSize,
                                    Extents = finalized
                                };
                                pendingExtents.Clear();
                                pendingMultiName = null;
                            }
                            else
                            {
                                pendingExtents.Clear();
                                pendingMultiName = null;
                                string norm = NormalizeRelativePath(relPath);
                                results[norm] = new IsoFileEntry
                                {
                                    RelativePath = norm,
                                    SizeBytes = dataLen,
                                    Extents = new[] { ext }
                                };
                            }
                        }
                    }
                }
            }

            pos += recLen;
        }
    }

    private static void ReadSector(FileStream fs, long sectorNumber, Span<byte> buffer) =>
        ReadAt(fs, sectorNumber * SectorSize, buffer);

    private static void ReadAt(FileStream fs, long byteOffset, Span<byte> buffer)
    {
        fs.Position = byteOffset;
        int total = 0;
        while (total < buffer.Length)
        {
            int n = fs.Read(buffer[total..]);
            if (n <= 0)
            {
                throw new EndOfStreamException("Unexpected EOF while reading ISO image.");
            }

            total += n;
        }
    }

    private static void ReadStreamFully(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }
    }

    /// <summary>
    /// Seekable read-only Stream mapped across one or more contiguous or non-contiguous byte extents inside an .iso FileStream.
    /// </summary>
    private sealed class IsoExtentSliceStream : Stream
    {
        private readonly FileStream _iso;
        private readonly IReadOnlyList<IsoByteExtent> _extents;
        private readonly long[] _logicalStarts;
        private readonly long _totalLength;
        private long _position;

        public IsoExtentSliceStream(FileStream iso, IReadOnlyList<IsoByteExtent> extents, long totalLength)
        {
            _iso = iso;
            _extents = extents;
            _totalLength = Math.Max(0L, totalLength);
            _logicalStarts = new long[extents.Count];

            long acc = 0;
            for (int i = 0; i < extents.Count; i++)
            {
                _logicalStarts[i] = acc;
                acc += extents[i].LengthBytes;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _totalLength;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _totalLength || buffer.Length == 0 || _extents.Count == 0)
            {
                return 0;
            }

            int maxToRead = (int)Math.Min(buffer.Length, _totalLength - _position);
            int extIdx = FindExtentIndex(_position);
            if (extIdx < 0)
            {
                return 0;
            }

            IsoByteExtent ext = _extents[extIdx];
            long offsetInExt = _position - _logicalStarts[extIdx];
            int chunkLen = (int)Math.Min(maxToRead, ext.LengthBytes - offsetInExt);
            if (chunkLen <= 0)
            {
                return 0;
            }

            if (ext.IsZeroSparse)
            {
                buffer[..chunkLen].Clear();
                _position += chunkLen;
                return chunkLen;
            }

            _iso.Position = ext.FileOffsetBytes + offsetInExt;
            int read = _iso.Read(buffer[..chunkLen]);
            if (read > 0)
            {
                _position += read;
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _totalLength || buffer.Length == 0 || _extents.Count == 0)
            {
                return 0;
            }

            int maxToRead = (int)Math.Min(buffer.Length, _totalLength - _position);
            int extIdx = FindExtentIndex(_position);
            if (extIdx < 0)
            {
                return 0;
            }

            IsoByteExtent ext = _extents[extIdx];
            long offsetInExt = _position - _logicalStarts[extIdx];
            int chunkLen = (int)Math.Min(maxToRead, ext.LengthBytes - offsetInExt);
            if (chunkLen <= 0)
            {
                return 0;
            }

            if (ext.IsZeroSparse)
            {
                buffer.Span[..chunkLen].Clear();
                _position += chunkLen;
                return chunkLen;
            }

            _iso.Position = ext.FileOffsetBytes + offsetInExt;
            int read = await _iso.ReadAsync(buffer[..chunkLen], cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _position += read;
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _totalLength + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            Position = target;
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int FindExtentIndex(long pos)
        {
            int lo = 0;
            int hi = _extents.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                long start = _logicalStarts[mid];
                long end = start + _extents[mid].LengthBytes;
                if (pos < start)
                {
                    hi = mid - 1;
                }
                else if (pos >= end)
                {
                    lo = mid + 1;
                }
                else
                {
                    return mid;
                }
            }

            return -1;
        }
    }
}
