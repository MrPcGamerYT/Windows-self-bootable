using System.Buffers.Binary;
using System.Text;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Native;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class SelfContainedNativeEngineTests
{
    [Fact]
    public void SwbiWorkDirectory_Detects8Dot3ShortSegments_AndNeverThrowsOnMissing8Dot3Cleanup()
    {
        Assert.True(SwbiWorkDirectory.ContainsBrokenShort8Dot3Segment(@"C:\Users\ZLAG~1\AppData\Local\Temp"));
        Assert.True(SwbiWorkDirectory.ContainsBrokenShort8Dot3Segment(@"C:\NONEXI~1\SWBI"));
        Assert.False(SwbiWorkDirectory.ContainsBrokenShort8Dot3Segment(@"C:\ProgramData\SWBI\Work"));
        Assert.False(SwbiWorkDirectory.ContainsBrokenShort8Dot3Segment(@"C:\Users\Z LAG\AppData\Local\SWBI"));

        // Exact regression guard for C:\Users\ZLAG~1 error: cleanup helpers must NEVER throw
        Exception? fileEx = Record.Exception(() =>
            SwbiWorkDirectory.TryDeleteFile(@"C:\Users\ZLAG~1\AppData\Local\Temp\swbi-dp-create-1234.txt"));
        Exception? dirEx = Record.Exception(() =>
            SwbiWorkDirectory.TryDeleteDirectory(@"C:\Users\ZLAG~1\AppData\Local\Temp\SWBI-Stage-1234"));

        Assert.Null(fileEx);
        Assert.Null(dirEx);

        string safeDir = SwbiWorkDirectory.EnsureSafeWorkDirectory("unit-test");
        Assert.True(Directory.Exists(safeDir));
        Assert.False(SwbiWorkDirectory.ContainsBrokenShort8Dot3Segment(safeDir));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void NativeFat32Formatter_WritesValidMicrosoftEfiFat32BootFsInfoTablesAndLabel(int bytesPerSector)
    {
        // Use 256 MiB for 512B sectors (~524k clusters >= 65525) or 300 MiB for 4Kn (clusters >= 65525)
        long volumeSizeBytes = bytesPerSector == 4096
            ? 300L * 1024 * 1024
            : 64L * 1024 * 1024;

        // Allocate sparse/resizable memory stream that only needs header + FAT + root cluster region
        using var stream = new SparseWindowStream(volumeSizeBytes);
        NativeFat32Formatter.FormatStream(stream, volumeSizeBytes, bytesPerSector, "SWBI-SETUP");

        // 1. Inspect Sector 0 (FAT32 Boot Sector)
        byte[] bootSector = stream.ReadSlice(0, bytesPerSector);
        Assert.Equal(0x55, bootSector[510]);
        Assert.Equal(0xAA, bootSector[511]);
        Assert.Equal("MSWIN4.1", Encoding.ASCII.GetString(bootSector, 3, 8));
        Assert.Equal((ushort)bytesPerSector, BinaryPrimitives.ReadUInt16LittleEndian(bootSector.AsSpan(11, 2)));
        Assert.Equal((ushort)32, BinaryPrimitives.ReadUInt16LittleEndian(bootSector.AsSpan(14, 2))); // ReservedSectors
        Assert.Equal(2, bootSector[16]); // NumFATs
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bootSector.AsSpan(44, 4))); // RootCluster = 2
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bootSector.AsSpan(48, 2))); // FSInfo sector = 1
        Assert.Equal((ushort)6, BinaryPrimitives.ReadUInt16LittleEndian(bootSector.AsSpan(50, 2))); // Backup boot = 6
        Assert.Equal(0x29, bootSector[66]); // Extended boot signature
        Assert.Equal("SWBI-SETUP ", Encoding.ASCII.GetString(bootSector, 71, 11));
        Assert.Equal("FAT32   ", Encoding.ASCII.GetString(bootSector, 82, 8));

        // 2. Inspect Sector 1 (FSInfo)
        byte[] fsInfo = stream.ReadSlice(bytesPerSector, bytesPerSector);
        Assert.Equal(0x41615252u, BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(0, 4)));
        Assert.Equal(0x61417272u, BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(484, 4)));
        Assert.Equal(0xAA550000u, BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(508, 4)));

        // 3. Inspect Sector 6 (Backup Boot Sector matches Sector 0)
        byte[] backupBoot = stream.ReadSlice(6L * bytesPerSector, bytesPerSector);
        Assert.Equal(bootSector, backupBoot);

        // 4. Inspect FAT #1 and FAT #2 initial 12 bytes + Root Directory Volume ID entry
        uint fatSizeSectors = BinaryPrimitives.ReadUInt32LittleEndian(bootSector.AsSpan(36, 4));
        byte[] fat1Head = stream.ReadSlice(32L * bytesPerSector, 16);
        byte[] fat2Head = stream.ReadSlice((32L + fatSizeSectors) * bytesPerSector, 16);
        Assert.Equal(0x0FFFFFF8u, BinaryPrimitives.ReadUInt32LittleEndian(fat1Head.AsSpan(0, 4)));
        Assert.Equal(0x0FFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(fat1Head.AsSpan(4, 4)));
        Assert.Equal(0x0FFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(fat1Head.AsSpan(8, 4)));
        Assert.Equal(fat1Head, fat2Head);

        long rootDirOffset = (32L + (2L * fatSizeSectors)) * bytesPerSector;
        byte[] rootEntry = stream.ReadSlice(rootDirOffset, 32);
        Assert.Equal("SWBI-SETUP ", Encoding.ASCII.GetString(rootEntry, 0, 11));
        Assert.Equal(0x08, rootEntry[11]); // ATTR_VOLUME_ID
    }

    [Fact]
    public void Win32StorageInterop_RoundTripsGptAndMbrDriveLayoutsCleanly()
    {
        Guid diskId = Guid.NewGuid();
        Guid espPartId = Guid.NewGuid();
        Guid osPartId = Guid.NewGuid();
        Guid swbiPartId = Guid.NewGuid();

        var originalGpt = new RawDiskLayoutSnapshot(
            PartitionStyle: Win32StorageInterop.PARTITION_STYLE_GPT,
            GptDiskId: diskId,
            GptStartingUsableOffset: 1048576L,
            GptUsableLength: 128L * 1024 * 1024 * 1024,
            GptMaxPartitionCount: 128,
            MbrSignature: 0,
            MbrCheckSum: 0,
            Partitions: Array.Empty<RawPartitionLayoutEntry>());

        var entriesWithRewrite = new (RawPartitionLayoutEntry Entry, bool Rewrite)[]
        {
            (new RawPartitionLayoutEntry(
                Win32StorageInterop.PARTITION_STYLE_GPT, 1048576L, 100L * 1024 * 1024, 1,
                Win32StorageInterop.GptEfiSystemTypeGuid, espPartId, 0, "EFI system partition",
                0, false, false, 0, Guid.Empty), false),
            (new RawPartitionLayoutEntry(
                Win32StorageInterop.PARTITION_STYLE_GPT, 106L * 1024 * 1024, 100L * 1024 * 1024 * 1024, 2,
                Win32StorageInterop.GptBasicDataTypeGuid, osPartId, 0, "Basic data partition",
                0, false, false, 0, Guid.Empty), false),
            (new RawPartitionLayoutEntry(
                Win32StorageInterop.PARTITION_STYLE_GPT, 101L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024, 3,
                Win32StorageInterop.GptBasicDataTypeGuid, swbiPartId, 0, "SWBI - Windows Bootable Setup",
                0, false, false, 0, Guid.Empty), true)
        };

        byte[] gptBytes = Win32StorageInterop.SerializeDiskLayout(originalGpt, entriesWithRewrite);
        Assert.Equal(48 + (3 * 144), gptBytes.Length);
        Assert.Equal(1, gptBytes[48 + (2 * 144) + 28]); // RewritePartition byte on 3rd entry
        Assert.Equal(0, gptBytes[48 + (1 * 144) + 28]); // RewritePartition byte on 2nd entry

        RawDiskLayoutSnapshot? parsedGpt = Win32StorageInterop.ParseDriveLayoutBuffer(gptBytes, gptBytes.Length);
        Assert.NotNull(parsedGpt);
        Assert.Equal(Win32StorageInterop.PARTITION_STYLE_GPT, parsedGpt!.PartitionStyle);
        Assert.Equal(diskId, parsedGpt.GptDiskId);
        Assert.Equal(3, parsedGpt.Partitions.Count);
        Assert.Equal(swbiPartId, parsedGpt.Partitions[2].GptPartitionId);
        Assert.Equal("SWBI - Windows Bootable Setup", parsedGpt.Partitions[2].GptName);

        // Also verify MBR primary slot in-place patch preserves Active (0x80) flag AND Extended/Logical EBR slots (4..7)!
        var mbrHeader = new RawDiskLayoutSnapshot(
            PartitionStyle: Win32StorageInterop.PARTITION_STYLE_MBR,
            GptDiskId: Guid.Empty,
            GptStartingUsableOffset: 0,
            GptUsableLength: 0,
            GptMaxPartitionCount: 0,
            MbrSignature: 0x1A2B3C4Du,
            MbrCheckSum: 0,
            Partitions: Array.Empty<RawPartitionLayoutEntry>());

        var emptySlot = new RawPartitionLayoutEntry(
            Win32StorageInterop.PARTITION_STYLE_MBR, 0, 0, 0, Guid.Empty, Guid.Empty, 0, string.Empty,
            0x00, false, false, 0, Guid.Empty);

        // 8-slot MBR buffer: Slot 0 = Active C: (0x07), Slot 1 = Extended container (0x0F), Slots 2..3 = unused, Slot 4 = Logical D: (0x07)
        var mbrRawSlots = new (RawPartitionLayoutEntry Entry, bool Rewrite)[]
        {
            (new RawPartitionLayoutEntry(
                Win32StorageInterop.PARTITION_STYLE_MBR, 1048576L, 80L * 1024 * 1024 * 1024, 1,
                Guid.Empty, Guid.Empty, 0, string.Empty,
                0x07, MbrBootIndicator: true, MbrRecognized: true, 2048u, Guid.NewGuid()), false),
            (new RawPartitionLayoutEntry(
                Win32StorageInterop.PARTITION_STYLE_MBR, 90L * 1024 * 1024 * 1024, 40L * 1024 * 1024 * 1024, 0,
                Guid.Empty, Guid.Empty, 0, string.Empty,
                0x0F, MbrBootIndicator: false, MbrRecognized: false, 188743680u, Guid.Empty), false),
            (emptySlot, false),
            (emptySlot, false),
            (new RawPartitionLayoutEntry(
                Win32StorageInterop.PARTITION_STYLE_MBR, 90L * 1024 * 1024 * 1024 + 1048576L, 39L * 1024 * 1024 * 1024, 2,
                Guid.Empty, Guid.Empty, 0, string.Empty,
                0x07, MbrBootIndicator: false, MbrRecognized: true, 2048u, Guid.NewGuid()), false),
            (emptySlot, false),
            (emptySlot, false),
            (emptySlot, false)
        };

        byte[] initialMbrBytes = Win32StorageInterop.SerializeDiskLayout(mbrHeader, mbrRawSlots);
        RawDiskLayoutSnapshot? parsedMbr = Win32StorageInterop.ParseDriveLayoutBuffer(initialMbrBytes, initialMbrBytes.Length);
        Assert.NotNull(parsedMbr);
        Assert.Equal(2, parsedMbr!.MbrPrimarySlotsUsed); // Slot 0 (C:) + Slot 1 (Extended container)
        Assert.True(parsedMbr.HasMbrExtendedPartition);

        var swbiMbrCandidate = new RawPartitionLayoutEntry(
            Win32StorageInterop.PARTITION_STYLE_MBR,
            80L * 1024 * 1024 * 1024 + 1048576L,
            8L * 1024 * 1024 * 1024,
            0,
            Guid.Empty, Guid.Empty, 0, string.Empty,
            0x0C, MbrBootIndicator: false, MbrRecognized: true, 0u, Guid.NewGuid());

        byte[]? patchedMbrBytes = Win32StorageInterop.TrySerializeMbrSlotPatchPreservingEbrs(
            parsedMbr,
            newPrimaryPartitionToAdd: swbiMbrCandidate,
            primarySlotIndexToDelete: null);

        Assert.NotNull(patchedMbrBytes);
        Assert.Equal(48 + (8 * 144), patchedMbrBytes!.Length); // Preserved all 8 slots (including EBRs 4..7)!
        Assert.Equal(0, patchedMbrBytes[48 + (0 * 144) + 28]); // Slot 0 RewritePartition == 0 (untouched!)
        Assert.Equal(1, patchedMbrBytes[48 + (0 * 144) + 33]); // Slot 0 MbrBootIndicator (Active 0x80) preserved!
        Assert.Equal(1, patchedMbrBytes[48 + (2 * 144) + 28]); // Free Primary Slot 2 RewritePartition == 1!
        Assert.Equal(0x0C, patchedMbrBytes[48 + (2 * 144) + 32]); // 0x0C FAT32 LBA!
        Assert.Equal(0, patchedMbrBytes[48 + (4 * 144) + 28]); // Logical EBR Slot 4 RewritePartition == 0 (100% preserved!)

        RawDiskLayoutSnapshot? parsedPatchedMbr = Win32StorageInterop.ParseDriveLayoutBuffer(patchedMbrBytes, patchedMbrBytes.Length);
        Assert.Equal(3, parsedPatchedMbr!.MbrPrimarySlotsUsed);
        Assert.Contains(parsedPatchedMbr.Partitions, p => p.RawSlotIndex == 4 && p.PartitionNumber == 2);
    }

    [Fact]
    public void NativeUefiAndBcdRegistryEngine_BuildsSpecCompliantUefiLoadOptionAndBcdDeviceBlobs()
    {
        Guid diskId = Guid.NewGuid();
        Guid partId = Guid.NewGuid();
        var layout = new RawDiskLayoutSnapshot(
            Win32StorageInterop.PARTITION_STYLE_GPT,
            diskId,
            1048576L,
            128L * 1024 * 1024 * 1024,
            128,
            0,
            0,
            Array.Empty<RawPartitionLayoutEntry>());

        var swbiPart = new RawPartitionLayoutEntry(
            Win32StorageInterop.PARTITION_STYLE_GPT,
            100L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024,
            4,
            Win32StorageInterop.GptBasicDataTypeGuid,
            partId,
            0,
            "SWBI",
            0, false, false, 0, Guid.Empty);

        byte[] uefiOpt = NativeUefiAndBcdRegistryEngine.BuildUefiLoadOptionBytes(
            SwbiBrand.BootChooserEntryName,
            layout,
            swbiPart,
            512,
            @"\EFI\BOOT\BOOTX64.EFI");

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(uefiOpt.AsSpan(0, 4))); // LOAD_OPTION_ACTIVE
        Assert.Contains(SwbiBrand.BootChooserEntryName, Encoding.Unicode.GetString(uefiOpt));
        // End node must end with 0x7F, 0xFF, 0x04, 0x00
        Assert.Equal(0x7F, uefiOpt[^4]);
        Assert.Equal(0xFF, uefiOpt[^3]);
        Assert.Equal(0x04, uefiOpt[^2]);
        Assert.Equal(0x00, uefiOpt[^1]);

        byte[] qualPart = NativeUefiAndBcdRegistryEngine.BuildBcdQualifiedPartitionElement(Guid.Empty, layout, swbiPart);
        Assert.Equal(88, qualPart.Length);
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(qualPart.AsSpan(16, 4))); // QualifiedPartition = 6
        Assert.Equal(partId, new Guid(qualPart.AsSpan(32, 16))); // PartitionGuid at [32..47] (dest72[16..31])
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(qualPart.AsSpan(48, 4))); // GPT = 0 at [48..51] (dest72[32..35])
        Assert.Equal(diskId, new Guid(qualPart.AsSpan(56, 16))); // DiskGuid at [56..71] (dest72[40..55])

        Guid sdiGuid = Guid.NewGuid();
        byte[] ramdiskBlob = NativeUefiAndBcdRegistryEngine.BuildBcdRamdiskDeviceElement(
            sdiGuid,
            layout,
            swbiPart,
            @"\sources\boot.wim");
        Assert.Equal(176, ramdiskBlob.Length);
        Assert.Equal(sdiGuid, new Guid(ramdiskBlob.AsSpan(0, 16)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(ramdiskBlob.AsSpan(16, 4))); // BlockIo = 0
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(ramdiskBlob.AsSpan(20, 4))); // Flags = 1 (AdditionalOptions present)
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(ramdiskBlob.AsSpan(32, 4))); // BlockIoType = 3 (Ramdisk)
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(ramdiskBlob.AsSpan(56, 4))); // FileDevice header = 1
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(ramdiskBlob.AsSpan(64, 4))); // FileDevice version = 5
        Assert.Equal(partId, new Guid(ramdiskBlob.AsSpan(68 + 16, 16)));
        Assert.Equal(diskId, new Guid(ramdiskBlob.AsSpan(68 + 40, 16)));
        Assert.Contains(@"\sources\boot.wim", Encoding.Unicode.GetString(ramdiskBlob));
    }

    [Fact]
    public void NativeDriverStoreScanner_ExportsOemStorageAndNetDrivers_SkipsMicrosoftInbox()
    {
        string tempRoot = SwbiWorkDirectory.CreateUniqueStagingDirectory("drvstore-test");
        try
        {
            string fakeRepo = Path.Combine(tempRoot, "FileRepository");
            string outA = Path.Combine(tempRoot, "OutWinPE");
            string outB = Path.Combine(tempRoot, "OutChooser");

            // 1. Intel VMD OEM Storage Driver (should export)
            string vmdDir = Path.Combine(fakeRepo, "iastorvd.inf_amd64_11223344");
            Directory.CreateDirectory(vmdDir);
            File.WriteAllText(Path.Combine(vmdDir, "iaStorVD.inf"), """
[Version]
Signature="$WINDOWS NT$"
Class=SCSIAdapter
ClassGUID={4D36E97B-E325-11CE-BFC1-08002BE10318}
Provider=%INTEL%

[Strings]
INTEL="Intel Corporation"
""", Encoding.Unicode);
            File.WriteAllBytes(Path.Combine(vmdDir, "iaStorVD.sys"), new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

            // 2. Microsoft Inbox storahci (should skip)
            string ahciDir = Path.Combine(fakeRepo, "storahci.inf_amd64_99887766");
            Directory.CreateDirectory(ahciDir);
            File.WriteAllText(Path.Combine(ahciDir, "storahci.inf"), """
[Version]
Class=HDC
Provider=%Msft%
[Strings]
Msft="Microsoft"
""");
            File.WriteAllBytes(Path.Combine(ahciDir, "storahci.sys"), new byte[] { 0x4D, 0x5A });

            // 3. Intel Wi-Fi OEM Network Driver
            string wifiDir = Path.Combine(fakeRepo, "netwtw10.inf_amd64_55667788");
            Directory.CreateDirectory(wifiDir);
            File.WriteAllText(Path.Combine(wifiDir, "Netwtw10.inf"), """
[Version]
Class=Net
ClassGUID={4d36e972-e325-11ce-bfc1-08002be10318}
Provider="Intel"
""");
            File.WriteAllBytes(Path.Combine(wifiDir, "Netwtw10.sys"), new byte[] { 0x4D, 0x5A });

            var logger = new SafeRedactingLogger();
            int storageExported = NativeDriverStoreScanner.ExportBootCriticalStorageDrivers(
                fakeRepo,
                new[] { outA, outB },
                activeControllerServiceName: null,
                logger);

            int netExported = NativeDriverStoreScanner.ExportOemNetworkDrivers(
                fakeRepo,
                new[] { outA },
                logger);

            Assert.Equal(1, storageExported);
            Assert.Equal(1, netExported);
            Assert.True(File.Exists(Path.Combine(outA, "iastorvd.inf_amd64_11223344", "iaStorVD.sys")));
            Assert.True(File.Exists(Path.Combine(outB, "iastorvd.inf_amd64_11223344", "iaStorVD.inf")));
            Assert.False(Directory.Exists(Path.Combine(outA, "storahci.inf_amd64_99887766")));
            Assert.True(File.Exists(Path.Combine(outA, "netwtw10.inf_amd64_55667788", "Netwtw10.sys")));
        }
        finally
        {
            SwbiWorkDirectory.TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ComputeLargestUnallocatedExtentBytes_FindsExactGapAfterShrunkOsPartition()
    {
        long diskBytes = 128L * 1024 * 1024 * 1024;
        var parts = new[]
        {
            new PartitionSnapshot { DiskNumber = 0, PartitionNumber = 1, OffsetBytes = 1048576L, SizeBytes = 100L * 1024 * 1024 },
            new PartitionSnapshot { DiskNumber = 0, PartitionNumber = 2, OffsetBytes = 106L * 1024 * 1024, SizeBytes = 100L * 1024 * 1024 * 1024 },
            new PartitionSnapshot { DiskNumber = 0, PartitionNumber = 3, OffsetBytes = 120L * 1024 * 1024 * 1024, SizeBytes = 600L * 1024 * 1024 }
        };

        long largestGap = WindowsDiskInspector.ComputeLargestUnallocatedExtentBytes(diskBytes, parts);
        long expectedBetweenOsAndRec = (120L * 1024 * 1024 * 1024) - ((106L * 1024 * 1024) + (100L * 1024 * 1024 * 1024));
        Assert.Equal(expectedBetweenOsAndRec, largestGap);
    }

    [Fact]
    public void PureCSharpWimSplitter_SplitsMultiResourceWim_And_PassesPureCSharpSwmVerification()
    {
        string tempDir = SwbiWorkDirectory.CreateUniqueStagingDirectory("pure-swm-unit");
        try
        {
            byte[] syntheticWim = BuildSyntheticMultiResourceWim(resourceCount: 6, resourceSizeBytes: 420 * 1024);
            using var wimStream = new MemoryStream(syntheticWim, writable: false);

            Assert.True(PureCSharpWimSplitter.CanSplitDirectly(wimStream));

            string firstSwm = Path.Combine(tempDir, "install.swm");
            IReadOnlyList<string> createdParts = PureCSharpWimSplitter.SplitWimStream(
                wimStream,
                firstSwm,
                chunkSizeMb: 1);

            Assert.True(createdParts.Count >= 3, $"Expected >=3 SWM parts, got {createdParts.Count}");
            Assert.Equal("install.swm", Path.GetFileName(createdParts[0]));
            Assert.Equal("install2.swm", Path.GetFileName(createdParts[1]));
            Assert.Equal("install3.swm", Path.GetFileName(createdParts[2]));

            foreach (string partPath in createdParts)
            {
                long len = new FileInfo(partPath).Length;
                Assert.InRange(len, 208L, 1024L * 1024L);
            }

            Assert.True(NativeWimApi.VerifyWimOrSwmIntegrity(firstSwm, out string verifyDetail), verifyDetail);
            Assert.Contains("SWM set", verifyDetail);
        }
        finally
        {
            SwbiWorkDirectory.TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void PureCSharpWimSplitter_RealWindowsWimgapi_RoundTripCaptureSplitAndApply_ByteIdentical()
    {
        if (!OperatingSystem.IsWindows() || !NativeWimApi.IsWimgapiAvailable())
        {
            return;
        }

        string tempDir = SwbiWorkDirectory.CreateUniqueStagingDirectory("wimgapi-roundtrip");
        try
        {
            string captureSrcDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(Path.Combine(captureSrcDir, "Windows", "System32"));
            Directory.CreateDirectory(Path.Combine(captureSrcDir, "Users", "Default"));

            // Create deterministic uncompressible binary payloads so the captured WIM exceeds 2.6 MiB
            var rng = new Random(20261027);
            byte[] fileA = new byte[950 * 1024];
            byte[] fileB = new byte[950 * 1024];
            byte[] fileC = new byte[950 * 1024];
            rng.NextBytes(fileA);
            rng.NextBytes(fileB);
            rng.NextBytes(fileC);

            string relA = Path.Combine("Windows", "System32", "ntoskrnl_mock.bin");
            string relB = Path.Combine("Windows", "System32", "win32k_mock.bin");
            string relC = Path.Combine("Users", "Default", "ntuser_mock.dat");
            string relTxt = Path.Combine("Windows", "swbi-marker.txt");

            File.WriteAllBytes(Path.Combine(captureSrcDir, relA), fileA);
            File.WriteAllBytes(Path.Combine(captureSrcDir, relB), fileB);
            File.WriteAllBytes(Path.Combine(captureSrcDir, relC), fileC);
            File.WriteAllText(Path.Combine(captureSrcDir, relTxt), "SWBI PureCSharpWimSplitter Verified!", Encoding.UTF8);

            // 1. Capture real Microsoft WIM using Windows wimgapi.dll
            string capturedWim = Path.Combine(tempDir, "captured.wim");
            NativeWimApi.CaptureDirectoryToWim(captureSrcDir, capturedWim);
            Assert.True(new FileInfo(capturedWim).Length > 2 * 1024 * 1024);

            // 2. Split into 1 MiB .swm chunks using 100% PURE C# PureCSharpWimSplitter!
            string swmDir = Path.Combine(tempDir, "swm");
            string firstSwm = Path.Combine(swmDir, "install.swm");
            IReadOnlyList<string> swmParts = PureCSharpWimSplitter.SplitWimFile(capturedWim, firstSwm, chunkSizeMb: 1);
            Assert.True(swmParts.Count >= 3, $"Expected >=3 SWM parts, got {swmParts.Count}");
            Assert.True(NativeWimApi.VerifyWimOrSwmIntegrity(firstSwm, out string swmVerifyMsg), swmVerifyMsg);

            // 3. Apply the pure-C#-generated .swm span set BACK using real Microsoft Windows wimgapi.dll
            //    (WIMCreateFile + WIMSetReferenceFile + WIMLoadImage + WIMApplyImage)!
            string applyDir = Path.Combine(tempDir, "applied");
            NativeWimApi.ApplySplitSwmToDirectory(
                swmParts[0],
                swmParts.Skip(1).ToArray(),
                applyDir);

            // 4. Verify 100% byte-for-byte identity across every extracted file!
            Assert.Equal(fileA, File.ReadAllBytes(Path.Combine(applyDir, relA)));
            Assert.Equal(fileB, File.ReadAllBytes(Path.Combine(applyDir, relB)));
            Assert.Equal(fileC, File.ReadAllBytes(Path.Combine(applyDir, relC)));
            Assert.Equal("SWBI PureCSharpWimSplitter Verified!", File.ReadAllText(Path.Combine(applyDir, relTxt), Encoding.UTF8));
        }
        finally
        {
            SwbiWorkDirectory.TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Win32StorageInterop_TryDetectTpmHardware_ExecutesCleanlyWithoutStaticTbsImport()
    {
        bool detected = Win32StorageInterop.TryDetectTpmHardware(out string version);
        Assert.NotNull(version);
        if (detected)
        {
            Assert.Contains(version, new[] { "2.0", "1.2" });
        }
    }

    [Fact]
    public void ReadinessEvaluator_Supports32BitX86_And_CrossBitness32To64Upgrade()
    {
        Assert.True(WindowsDiskInspector.DetectCpuSupports64Bit());

        var evaluator = new ReadinessEvaluator(new PartitionSizingCalculator(), new SmartCompatibilityAnalyzer());
        var baseScenario = UsbFreeInstaller.Core.Services.Simulation.SimulatedEnvironmentProvider.GetBuiltInScenarios()[0];

        // 1. 32-bit Windows 10 host (x86) on 64-bit CPU (Legacy BIOS) + 64-bit Windows 11 ISO (x64) -> PASS!
        SystemEnvironmentSnapshot x86HostWith64Cpu = baseScenario.Environment with
        {
            HostArchitecture = "x86",
            Firmware = FirmwareMode.LegacyBios,
            Hardware = new PcHardwareCapabilities
            {
                CpuName = "Intel Core i5 (running 32-bit Win10)",
                LogicalCores = 4,
                TotalRamBytes = 8L * ByteFormatting.OneGb,
                CpuSupports64Bit = true
            }
        };

        IsoInspectionResult x64Iso = baseScenario.SampleIso with { Architecture = "x64" };
        ReadinessReport upgrade32To64 = evaluator.Evaluate(x86HostWith64Cpu, x64Iso);
        Assert.DoesNotContain(upgrade32To64.Checks, c => c.Id == "iso-arch" && c.Severity == CheckSeverity.Blocker);
        Assert.Contains(upgrade32To64.Checks, c => c.Id == "iso-validation" && c.Summary.Contains("32-bit → 64-bit"));

        // 2. 32-bit Windows 10 host (x86) + 32-bit Windows 10 ISO (x86) -> PASS!
        IsoInspectionResult x86Iso = baseScenario.SampleIso with { Architecture = "x86" };
        ReadinessReport native32To32 = evaluator.Evaluate(x86HostWith64Cpu, x86Iso);
        Assert.DoesNotContain(native32To32.Checks, c => c.Id == "iso-arch" && c.Severity == CheckSeverity.Blocker);

        // 3. True 32-bit-only CPU (CpuSupports64Bit = false) + 64-bit ISO (x64) -> BLOCKER!
        SystemEnvironmentSnapshot pure32CpuHost = x86HostWith64Cpu with
        {
            Hardware = x86HostWith64Cpu.Hardware! with { CpuSupports64Bit = false }
        };
        ReadinessReport blockedOn32OnlyCpu = evaluator.Evaluate(pure32CpuHost, x64Iso);
        Assert.Contains(blockedOn32OnlyCpu.Checks, c => c.Id == "iso-arch" && c.Severity == CheckSeverity.Blocker);
    }

    private static byte[] BuildSyntheticMultiResourceWim(int resourceCount, int resourceSizeBytes)
    {
        using var ms = new MemoryStream();
        byte[] hdr = new byte[208];
        "MSWIM\0\0\0"u8.CopyTo(hdr.AsSpan(0, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8, 4), 208u);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12, 4), 0x00010D00u);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(16, 4), 0x00020000u); // XPRESS
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(20, 4), 32768u);
        Guid.NewGuid().TryWriteBytes(hdr.AsSpan(24, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(40, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(42, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(44, 4), 1u);
        ms.Write(hdr);

        var rng = new Random(42);
        var entries = new List<(long Offset, long Size, byte Flags, byte[] Hash)>();

        // 1 metadata resource + N file resources
        for (int i = 0; i <= resourceCount; i++)
        {
            long off = ms.Position;
            int sz = i == 0 ? 4096 : resourceSizeBytes;
            byte[] data = new byte[sz];
            rng.NextBytes(data);
            ms.Write(data);

            byte[] hash = System.Security.Cryptography.SHA1.HashData(data);
            byte flags = i == 0 ? (byte)0x02 /* METADATA */ : (byte)0x04 /* COMPRESSED */;
            entries.Add((off, sz, flags, hash));
        }

        // Offset table (50 bytes per entry)
        long otOff = ms.Position;
        byte[] otBytes = new byte[entries.Count * 50];
        for (int i = 0; i < entries.Count; i++)
        {
            Span<byte> e = otBytes.AsSpan(i * 50, 50);
            ulong sizeAndFlags = ((ulong)entries[i].Size & 0x00FFFFFFFFFFFFFFUL) | ((ulong)entries[i].Flags << 56);
            BinaryPrimitives.WriteUInt64LittleEndian(e.Slice(0, 8), sizeAndFlags);
            BinaryPrimitives.WriteInt64LittleEndian(e.Slice(8, 8), entries[i].Offset);
            BinaryPrimitives.WriteInt64LittleEndian(e.Slice(16, 8), entries[i].Size);
            BinaryPrimitives.WriteUInt16LittleEndian(e.Slice(24, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(26, 4), 1u);
            entries[i].Hash.CopyTo(e.Slice(30, 20));
        }
        ms.Write(otBytes);

        // UTF-16LE XML metadata
        long xmlOff = ms.Position;
        const string xml = "<WIM><IMAGE INDEX=\"1\"><NAME>Windows 11 Pro</NAME><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>26100</BUILD></VERSION></WINDOWS></IMAGE></WIM>";
        byte[] xmlBytes = Encoding.Unicode.GetBytes("\uFEFF" + xml);
        ms.Write(xmlBytes);

        byte[] full = ms.ToArray();
        // Patch BootMetadata (offset 0x60), OffsetTable (offset 0x30), XmlData (offset 0x48)
        WriteResHdr(full.AsSpan(48, 24), otBytes.Length, 0x02, otOff, otBytes.Length);
        WriteResHdr(full.AsSpan(72, 24), xmlBytes.Length, 0x02, xmlOff, xmlBytes.Length);
        WriteResHdr(full.AsSpan(96, 24), entries[0].Size, entries[0].Flags, entries[0].Offset, entries[0].Size);
        return full;

        static void WriteResHdr(Span<byte> dst, long compSize, byte flags, long offset, long origSize)
        {
            ulong raw = ((ulong)compSize & 0x00FFFFFFFFFFFFFFUL) | ((ulong)flags << 56);
            BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(0, 8), raw);
            BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(8, 8), offset);
            BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(16, 8), origSize);
        }
    }

    /// <summary>
    /// Sparse in-memory stream so we can test 64 MiB - 300 MiB FAT32 formatting in <5 ms using tiny memory.
    /// </summary>
    private sealed class SparseWindowStream : Stream
    {
        private readonly Dictionary<long, byte[]> _pages = new();
        private const int PageShift = 16; // 64 KiB pages
        private const int PageSize = 1 << PageShift;
        private long _length;
        private long _position;

        public SparseWindowStream(long initialLength) => _length = initialLength;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int readTotal = 0;
            while (count > 0 && _position < _length)
            {
                long pageIdx = _position >> PageShift;
                int pageOff = (int)(_position & (PageSize - 1));
                int take = (int)Math.Min(count, Math.Min(PageSize - pageOff, _length - _position));

                if (_pages.TryGetValue(pageIdx, out byte[]? page))
                {
                    Buffer.BlockCopy(page, pageOff, buffer, offset, take);
                }
                else
                {
                    Array.Clear(buffer, offset, take);
                }

                _position += take;
                offset += take;
                count -= take;
                readTotal += take;
            }
            return readTotal;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                long pageIdx = _position >> PageShift;
                int pageOff = (int)(_position & (PageSize - 1));
                int take = Math.Min(count, PageSize - pageOff);

                if (!_pages.TryGetValue(pageIdx, out byte[]? page))
                {
                    page = new byte[PageSize];
                    _pages[pageIdx] = page;
                }

                Buffer.BlockCopy(buffer, offset, page, pageOff, take);
                _position += take;
                if (_position > _length) _length = _position;
                offset += take;
                count -= take;
            }
        }

        public byte[] ReadSlice(long offset, int length)
        {
            _position = offset;
            byte[] buf = new byte[length];
            int r = Read(buf, 0, length);
            Assert.Equal(length, r);
            return buf;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => _position
            };
            return _position;
        }

        public override void SetLength(long value) => _length = value;
    }
}
