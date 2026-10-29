using System.Buffers.Binary;
using System.Text;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Simulation;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class UniversalIsoAndResilienceTests
{
    [Fact]
    public void PowerShellCliXmlCleaner_DecodesExactUserScreenshotErrorsCleanly()
    {
        const string rawCliXml =
            "#< CLIXML\r\n" +
            "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
            "<S S=\"Error\">Get-Partition : No MSFT_Partition objects found with property 'DriveLetter' equal to 'C'.  Verify the value of the _x000D__x000A_</S>" +
            "<S S=\"Error\">property and retry._x000D__x000A_</S>" +
            "<S S=\"Error\">At line:42 char:11_x000D__x000A_</S>" +
            "<S S=\"Error\">+ $osPart = Get-Partition -DriveLetter $sysLetter_x000D__x000A_</S>" +
            "<S S=\"Error\">    + CategoryInfo          : ObjectNotFound: (C:Char) [Get-Partition], CimJobException_x000D__x000A_</S>" +
            "</Objs>";

        string cleaned = PowerShellCliXmlCleaner.Clean(rawCliXml);

        Assert.DoesNotContain("CLIXML", cleaned);
        Assert.DoesNotContain("_x000D_", cleaned);
        Assert.DoesNotContain("<Objs", cleaned);
        Assert.DoesNotContain("CategoryInfo", cleaned);
        Assert.Contains("No MSFT_Partition objects found with property 'DriveLetter' equal to 'C'.", cleaned);
    }

    [Fact]
    public void WimHeaderXmlParser_ParsesBinaryWimStreamWithUtf16XmlAndMultipleEditions()
    {
        const string xml = """
<WIM>
  <TOTALBYTES>5421000123</TOTALBYTES>
  <IMAGE INDEX="1">
    <NAME>Windows 11 Home</NAME>
    <DESCRIPTION>Windows 11 Home</DESCRIPTION>
    <TOTALBYTES>18200100200</TOTALBYTES>
    <WINDOWS>
      <ARCH>9</ARCH>
      <EDITIONID>Core</EDITIONID>
      <VERSION>
        <MAJOR>10</MAJOR>
        <MINOR>0</MINOR>
        <BUILD>26100</BUILD>
        <SPBUILD>1742</SPBUILD>
      </VERSION>
    </WINDOWS>
  </IMAGE>
  <IMAGE INDEX="2">
    <NAME>Windows 11 Pro</NAME>
    <DESCRIPTION>Windows 11 Pro</DESCRIPTION>
    <TOTALBYTES>18900100200</TOTALBYTES>
    <WINDOWS>
      <ARCH>9</ARCH>
      <EDITIONID>Professional</EDITIONID>
      <VERSION>
        <MAJOR>10</MAJOR>
        <MINOR>0</MINOR>
        <BUILD>26100</BUILD>
        <SPBUILD>1742</SPBUILD>
      </VERSION>
    </WINDOWS>
  </IMAGE>
</WIM>
""";

        byte[] wimBytes = BuildSyntheticWimBinary(xml, includeBom: true);
        using var ms = new MemoryStream(wimBytes);

        bool ok = WimHeaderXmlParser.TryParse(ms, out ParsedWimMetadata? meta);

        Assert.True(ok);
        Assert.NotNull(meta);
        Assert.Equal("x64", meta!.PrimaryArchitecture);
        Assert.Equal("10.0.26100", meta.PrimaryVersion);
        Assert.Equal(2, meta.Editions.Count);
        Assert.Equal("Windows 11 Home", meta.Editions[0].Name);
        Assert.Equal("Windows 11 Pro", meta.Editions[1].Name);
    }

    [Fact]
    public async Task WindowsIsoInspector_InspectsOfficialIsoDirectlyInPureCSharp_ZeroPowerShellCalls()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"swbi-test-official-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string isoPath = Path.Combine(tempDir, "Win11_24H2_Official_x64.iso");

        try
        {
            byte[] bootWim = BuildSyntheticWimBinary("""
<WIM>
  <IMAGE INDEX="1"><NAME>Microsoft Windows PE (x64)</NAME><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>26100</BUILD></VERSION></WINDOWS></IMAGE>
  <IMAGE INDEX="2"><NAME>Microsoft Windows Setup (x64)</NAME><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>26100</BUILD></VERSION></WINDOWS></IMAGE>
</WIM>
""");
            byte[] installWim = BuildSyntheticWimBinary("""
<WIM>
  <IMAGE INDEX="1"><NAME>Windows 11 Home</NAME><TOTALBYTES>18000000000</TOTALBYTES><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>26100</BUILD></VERSION></WINDOWS></IMAGE>
  <IMAGE INDEX="2"><NAME>Windows 11 Pro</NAME><TOTALBYTES>19000000000</TOTALBYTES><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>26100</BUILD></VERSION></WINDOWS></IMAGE>
</WIM>
""");

            WriteSyntheticIso9660Image(isoPath, new Dictionary<string, byte[]>
            {
                [@"setup.exe"] = Encoding.ASCII.GetBytes("MZ-SETUP-STUB"),
                [@"boot\boot.sdi"] = Encoding.ASCII.GetBytes("SDI-STUB-DATA"),
                [@"efi\boot\bootx64.efi"] = Encoding.ASCII.GetBytes("MZ-EFI-BOOTLOADER"),
                [@"sources\boot.wim"] = bootWim,
                [@"sources\install.wim"] = installWim
            });

            var throwingRunner = new ThrowingProcessRunner();
            var inspector = new WindowsIsoInspector(throwingRunner, new SafeRedactingLogger());

            IsoInspectionResult result = await inspector.InspectIsoAsync(isoPath, computeSha256: true);

            Assert.True(result.IsValid);
            Assert.False(result.IsCustomOrModifiedIso);
            Assert.Equal("Standard / Official ISO", result.IsoVariantBadge);
            Assert.Equal("x64", result.Architecture);
            Assert.Equal(InstallPayloadKind.Wim, result.PayloadKind);
            Assert.Equal(@"sources\install.wim", result.PayloadRelativePath);
            Assert.Equal(2, result.Editions.Count);
            Assert.NotNull(result.Sha256Hex);
            Assert.Equal(0, throwingRunner.CallCount);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task WindowsIsoInspector_SupportsStrippedCustomLiteIso_WithoutSetupExeBootSdiOrEfi()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"swbi-test-custom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string isoPath = Path.Combine(tempDir, "Tiny11_24H2_Superlite_Custom.iso");

        try
        {
            byte[] bootWim = BuildSyntheticWimBinary("""
<WIM>
  <IMAGE INDEX="1"><NAME>WinPE Setup</NAME><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>26100</BUILD></VERSION></WINDOWS></IMAGE>
</WIM>
""");
            byte[] customEsd = BuildSyntheticWimBinary("""
<WIM>
  <IMAGE INDEX="1"><NAME>Tiny11 24H2 Superlite</NAME><TOTALBYTES>7500000000</TOTALBYTES><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>26100</BUILD></VERSION></WINDOWS></IMAGE>
</WIM>
""");

            // Stripped custom ISO: NO setup.exe, NO boot\boot.sdi, NO efi\boot\bootx64.efi, includes autounattend.xml
            WriteSyntheticIso9660Image(isoPath, new Dictionary<string, byte[]>
            {
                [@"autounattend.xml"] = Encoding.UTF8.GetBytes("<unattend/>"),
                [@"sources\boot.wim"] = bootWim,
                [@"sources\install.esd"] = customEsd
            });

            var inspector = new WindowsIsoInspector(new ThrowingProcessRunner(), new SafeRedactingLogger());
            IsoInspectionResult result = await inspector.InspectIsoAsync(isoPath, computeSha256: false);

            Assert.True(result.IsValid);
            Assert.True(result.IsCustomOrModifiedIso);
            Assert.Equal("Custom / Modified ISO", result.IsoVariantBadge);
            Assert.False(result.HasSetupExe);
            Assert.False(result.HasBootSdi);
            Assert.False(result.HasEfiBootloader);
            Assert.Equal(InstallPayloadKind.Esd, result.PayloadKind);
            Assert.Single(result.Editions);
            Assert.Equal("Tiny11 24H2 Superlite", result.Editions[0].Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task WindowsIsoInspector_SupportsDualArchMctIsoAndCustomWimNames()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"swbi-test-dual-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dualIsoPath = Path.Combine(tempDir, "Windows10_DualArch_x86_x64.iso");

        try
        {
            byte[] x64Boot = BuildSyntheticWimBinary("""
<WIM><IMAGE INDEX="1"><NAME>PE x64</NAME><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>19045</BUILD></VERSION></WINDOWS></IMAGE></WIM>
""");
            byte[] x64Install = BuildSyntheticWimBinary("""
<WIM><IMAGE INDEX="1"><NAME>Windows 10 Pro</NAME><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>19045</BUILD></VERSION></WINDOWS></IMAGE></WIM>
""");
            byte[] x86Install = BuildSyntheticWimBinary("""
<WIM><IMAGE INDEX="1"><NAME>Windows 10 Pro x86</NAME><WINDOWS><ARCH>0</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>19045</BUILD></VERSION></WINDOWS></IMAGE></WIM>
""");

            WriteSyntheticIso9660Image(dualIsoPath, new Dictionary<string, byte[]>
            {
                [@"setup.exe"] = Encoding.ASCII.GetBytes("MZ"),
                [@"x64\boot\boot.sdi"] = Encoding.ASCII.GetBytes("SDI"),
                [@"x64\sources\boot.wim"] = x64Boot,
                [@"x64\sources\install.esd"] = x64Install,
                [@"x86\sources\install.esd"] = x86Install,
                [@"efi\boot\bootx64.efi"] = Encoding.ASCII.GetBytes("EFI")
            });

            var inspector = new WindowsIsoInspector(new ThrowingProcessRunner(), new SafeRedactingLogger());
            IsoInspectionResult result = await inspector.InspectIsoAsync(dualIsoPath, computeSha256: false);

            Assert.True(result.IsValid);
            Assert.Equal(@"x64\sources\boot.wim", result.BootWimRelativePath);
            Assert.Equal(@"x64\sources\install.esd", result.PayloadRelativePath);
            Assert.Equal("Windows 10 Pro", result.Editions[0].Name);

            // Also verify direct file extraction via IsoImageStreamReader
            using IsoImageStreamReader reader = IsoImageStreamReader.Open(dualIsoPath);
            Assert.True(reader.TryGetFile(@"x64\sources\install.esd", out IsoFileEntry? entry));
            string extracted = Path.Combine(tempDir, "extracted.esd");
            await reader.ExtractFileAsync(entry!, extracted, new byte[64 * 1024]);
            Assert.Equal(x64Install, await File.ReadAllBytesAsync(extracted));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ReadinessAndCompatibility_CustomTiny11OnLowRamNoTpmPc_HasZeroBlockersAndEnablesBypass()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateCustomTiny11OlderPcScenario();
        var evaluator = new ReadinessEvaluator(new PartitionSizingCalculator());

        ReadinessReport report = evaluator.Evaluate(scenario.Environment with { IsSimulated = false }, scenario.SampleIso);

        Assert.Equal(0, report.BlockerCount);
        Assert.True(report.CanSimulateDryRun);
        Assert.True(report.CanProceedInLiveMode);
        Assert.NotNull(report.Compatibility);
        Assert.True(report.Compatibility!.RecommendedOptions.BypassWin11HardwareChecks);
        Assert.False(report.Compatibility.SupportsInPlaceKeepAppsUpgrade); // Custom ISO omits root setup.exe
        Assert.Contains(report.Checks, c => c.Id == "custom-iso-profile" && c.Severity == CheckSeverity.Pass);
    }

    [Fact]
    public async Task WindowsIsoInspector_ReadsUdfBridgeIsoOverIso9660ReadmeStub()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"swbi-test-udf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string udfIsoPath = Path.Combine(tempDir, "Win11_UDF_Bridge.iso");

        try
        {
            byte[] bootWim = BuildSyntheticWimBinary("""
<WIM><IMAGE INDEX="1"><NAME>WinPE UDF</NAME><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>26100</BUILD></VERSION></WINDOWS></IMAGE></WIM>
""");
            byte[] installWim = BuildSyntheticWimBinary("""
<WIM><IMAGE INDEX="1"><NAME>Windows 11 Enterprise LTSC</NAME><WINDOWS><ARCH>9</ARCH><VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>26100</BUILD></VERSION></WINDOWS></IMAGE></WIM>
""");

            WriteSyntheticUdfBridgeIsoImage(udfIsoPath, bootWim, installWim);

            var inspector = new WindowsIsoInspector(new ThrowingProcessRunner(), new SafeRedactingLogger());
            IsoInspectionResult result = await inspector.InspectIsoAsync(udfIsoPath, computeSha256: false);

            Assert.True(result.IsValid);
            Assert.True(result.HasSetupExe);
            Assert.True(result.HasBootWim);
            Assert.Equal(@"sources\install.wim", result.PayloadRelativePath);
            Assert.Single(result.Editions);
            Assert.Equal("Windows 11 Enterprise LTSC", result.Editions[0].Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void WriteSyntheticUdfBridgeIsoImage(string outputPath, byte[] bootWim, byte[] installWim)
    {
        const int sec = 2048;
        const int partStart = 260;
        byte[] img = new byte[280 * sec];

        // 1. Write an ISO-9660 bridge stub at sector 16 with ONLY README.TXT (mimicking oscdimg -u2 UDF bridge ISOs)
        WriteVolumeDescriptor(img, 16, vdType: 1, rootSector: 20, rootLen: sec, totalSectors: 280, isJoliet: false);
        img[17 * sec] = 255;
        Encoding.ASCII.GetBytes("CD001").CopyTo(img, (17 * sec) + 1);
        img[(17 * sec) + 6] = 1;

        int p = 20 * sec;
        p += WriteDirRecord(img, p, new byte[] { 0 }, 20, sec, isDir: true);
        p += WriteDirRecord(img, p, new byte[] { 1 }, 20, sec, isDir: true);
        byte[] readmeData = Encoding.ASCII.GetBytes("This disc contains a UDF file system.");
        Buffer.BlockCopy(readmeData, 0, img, 21 * sec, readmeData.Length);
        WriteDirRecord(img, p, Encoding.ASCII.GetBytes("README.TXT;1"), 21, readmeData.Length, isDir: false);

        // 2. Write UDF Anchor Volume Descriptor Pointer (Tag 2) at Sector 256 -> VDS at Sector 32 (16 sectors)
        int avdp = 256 * sec;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(avdp, 2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(avdp + 16, 4), 16 * sec);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(avdp + 20, 4), 32);

        // Sector 32: Partition Descriptor (Tag 5), PartitionNumber=0, Start=260, Length=20
        int pd = 32 * sec;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(pd, 2), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(pd + 22, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(pd + 188, 4), partStart);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(pd + 192, 4), 20);

        // Sector 33: Logical Volume Descriptor (Tag 6), BlockSize=2048, FSD at LBN 0 PartRef 0, 1 Type-1 Partition Map
        int lvd = 33 * sec;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(lvd, 2), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(lvd + 212, 4), sec);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(lvd + 248, 4), sec);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(lvd + 252, 4), 0); // FSD LBN 0
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(lvd + 256, 2), 0); // PartRef 0
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(lvd + 264, 4), 6); // MapTableLength
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(lvd + 268, 4), 1); // NumPartitionMaps
        img[lvd + 440] = 1; // Type 1 map
        img[lvd + 441] = 6; // Length 6
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(lvd + 444, 2), 0); // PartitionNumber 0

        // Sector 34: Terminating Descriptor (Tag 8)
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(34 * sec, 2), 8);

        // Partition LBN 0 (Sector 260): File Set Descriptor (Tag 256) -> Root ICB at LBN 1, PartRef 0
        int fsd = (partStart + 0) * sec;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(fsd, 2), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(fsd + 400, 4), sec);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(fsd + 404, 4), 1); // Root ICB LBN 1
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(fsd + 408, 2), 0);

        // Partition LBN 1 (Sector 261): Root Extended File Entry (Tag 266, fileType=4 dir, inline adType=3)
        byte[] rootFids = ConcatBytes(
            BuildUdfFid(isDir: true, isParent: true, lbn: 1, nameCs0: Array.Empty<byte>()),
            BuildUdfFid(isDir: true, isParent: false, lbn: 2, nameCs0: EncodeCs0Ascii("sources")),
            BuildUdfFid(isDir: false, isParent: false, lbn: 3, nameCs0: EncodeCs0Ascii("setup.exe")));
        WriteUdfExtendedFileEntryInline(img, (partStart + 1) * sec, fileType: 4, rootFids);

        // Partition LBN 2 (Sector 262): 'sources' Standard File Entry (Tag 261, fileType=4 dir, inline adType=3)
        byte[] sourcesFids = ConcatBytes(
            BuildUdfFid(isDir: true, isParent: true, lbn: 1, nameCs0: Array.Empty<byte>()),
            BuildUdfFid(isDir: false, isParent: false, lbn: 4, nameCs0: EncodeCs0Utf16Be("boot.wim")),
            BuildUdfFid(isDir: false, isParent: false, lbn: 5, nameCs0: EncodeCs0Ascii("install.wim")));
        WriteUdfStandardFileEntryInline(img, (partStart + 2) * sec, fileType: 4, sourcesFids);

        // Partition LBN 3 (Sector 263): setup.exe inline file (Tag 261, fileType=5, adType=3)
        WriteUdfStandardFileEntryInline(img, (partStart + 3) * sec, fileType: 5, Encoding.ASCII.GetBytes("MZ-UDF-SETUP"));

        // Partition LBN 4 (Sector 264): boot.wim File Entry (Tag 261, short_ad -> LBN 6)
        Buffer.BlockCopy(bootWim, 0, img, (partStart + 6) * sec, bootWim.Length);
        WriteUdfStandardFileEntryShortAd(img, (partStart + 4) * sec, dataLbn: 6, dataLen: bootWim.Length);

        // Partition LBN 5 (Sector 265): install.wim Extended File Entry (Tag 266, long_ad -> LBN 8, PartRef 0)
        Buffer.BlockCopy(installWim, 0, img, (partStart + 8) * sec, installWim.Length);
        WriteUdfExtendedFileEntryLongAd(img, (partStart + 5) * sec, dataLbn: 8, partRef: 0, dataLen: installWim.Length);

        File.WriteAllBytes(outputPath, img);
    }

    private static void WriteUdfExtendedFileEntryInline(byte[] img, int off, byte fileType, byte[] inlinePayload)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(off, 2), 266);
        img[off + 27] = fileType;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(off + 34, 2), 3); // adType = 3 (inline)
        BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(off + 56, 8), inlinePayload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(off + 208, 4), 0); // L_EA
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(off + 212, 4), inlinePayload.Length); // L_AD
        Buffer.BlockCopy(inlinePayload, 0, img, off + 216, inlinePayload.Length);
    }

    private static void WriteUdfStandardFileEntryInline(byte[] img, int off, byte fileType, byte[] inlinePayload)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(off, 2), 261);
        img[off + 27] = fileType;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(off + 34, 2), 3); // adType = 3 (inline)
        BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(off + 56, 8), inlinePayload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(off + 168, 4), 0); // L_EA
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(off + 172, 4), inlinePayload.Length); // L_AD
        Buffer.BlockCopy(inlinePayload, 0, img, off + 176, inlinePayload.Length);
    }

    private static void WriteUdfStandardFileEntryShortAd(byte[] img, int off, uint dataLbn, int dataLen)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(off, 2), 261);
        img[off + 27] = 5; // Regular file
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(off + 34, 2), 0); // adType = 0 (short_ad)
        BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(off + 56, 8), dataLen);
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(off + 168, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(off + 172, 4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 176, 4), (uint)dataLen);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 180, 4), dataLbn);
    }

    private static void WriteUdfExtendedFileEntryLongAd(byte[] img, int off, uint dataLbn, ushort partRef, int dataLen)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(off, 2), 266);
        img[off + 27] = 5;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(off + 34, 2), 1); // adType = 1 (long_ad)
        BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(off + 56, 8), dataLen);
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(off + 208, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(off + 212, 4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 216, 4), (uint)dataLen);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 220, 4), dataLbn);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(off + 224, 2), partRef);
    }

    private static byte[] BuildUdfFid(bool isDir, bool isParent, uint lbn, byte[] nameCs0)
    {
        int rawLen = 38 + nameCs0.Length;
        int paddedLen = (rawLen + 3) & ~3;
        byte[] fid = new byte[paddedLen];
        BinaryPrimitives.WriteUInt16LittleEndian(fid.AsSpan(0, 2), 257); // Tag 257 FID
        byte chars = 0;
        if (isDir) chars |= 0x02;
        if (isParent) chars |= 0x08;
        fid[18] = chars;
        fid[19] = (byte)nameCs0.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(fid.AsSpan(20, 4), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(fid.AsSpan(24, 4), lbn);
        BinaryPrimitives.WriteUInt16LittleEndian(fid.AsSpan(28, 2), 0); // PartRef 0
        Buffer.BlockCopy(nameCs0, 0, fid, 38, nameCs0.Length);
        return fid;
    }

    private static byte[] EncodeCs0Ascii(string s)
    {
        byte[] b = new byte[1 + s.Length];
        b[0] = 8;
        Encoding.Latin1.GetBytes(s, b.AsSpan(1));
        return b;
    }

    private static byte[] EncodeCs0Utf16Be(string s)
    {
        byte[] u = Encoding.BigEndianUnicode.GetBytes(s);
        byte[] b = new byte[1 + u.Length];
        b[0] = 16;
        Buffer.BlockCopy(u, 0, b, 1, u.Length);
        return b;
    }

    private static byte[] ConcatBytes(params byte[][] arrays)
    {
        int total = arrays.Sum(a => a.Length);
        byte[] res = new byte[total];
        int pos = 0;
        foreach (byte[] a in arrays)
        {
            Buffer.BlockCopy(a, 0, res, pos, a.Length);
            pos += a.Length;
        }
        return res;
    }

    internal static byte[] BuildSyntheticWimBinary(string xmlText, bool includeBom = true)
    {
        byte[] xmlUtf16 = Encoding.Unicode.GetBytes(xmlText);
        byte[] xmlPayload;
        if (includeBom)
        {
            xmlPayload = new byte[2 + xmlUtf16.Length];
            xmlPayload[0] = 0xFF;
            xmlPayload[1] = 0xFE;
            Buffer.BlockCopy(xmlUtf16, 0, xmlPayload, 2, xmlUtf16.Length);
        }
        else
        {
            xmlPayload = xmlUtf16;
        }

        const int headerSize = 208;
        byte[] fullWim = new byte[headerSize + xmlPayload.Length];

        // "MSWIM\0\0\0" magic
        Encoding.ASCII.GetBytes("MSWIM\0\0\0").CopyTo(fullWim, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(fullWim.AsSpan(8, 4), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(fullWim.AsSpan(12, 4), 0x00010D00); // WIM version 1.13
        BinaryPrimitives.WriteUInt32LittleEndian(fullWim.AsSpan(44, 4), 1);          // Image count

        // WIM_RESHDR_DISK_SHORT rhXmlData at offset 72:
        // [0..7]: size (low 56 bits) + flags (byte 7 = 0x02 RESHDR_FLAG_METADATA)
        // [8..15]: offset in WIM (208)
        // [16..23]: uncompressed original size
        ulong sizeAndFlags = ((ulong)xmlPayload.Length & 0x00FF_FFFF_FFFF_FFFFUL) | ((ulong)0x02 << 56);
        BinaryPrimitives.WriteUInt64LittleEndian(fullWim.AsSpan(72, 8), sizeAndFlags);
        BinaryPrimitives.WriteInt64LittleEndian(fullWim.AsSpan(80, 8), headerSize);
        BinaryPrimitives.WriteInt64LittleEndian(fullWim.AsSpan(88, 8), xmlPayload.Length);

        Buffer.BlockCopy(xmlPayload, 0, fullWim, headerSize, xmlPayload.Length);
        return fullWim;
    }

    internal static void WriteSyntheticIso9660Image(string outputIsoPath, IReadOnlyDictionary<string, byte[]> files)
    {
        const int sectorSize = 2048;

        // Build directory tree
        var rootNode = new DirNode("");
        foreach ((string rawPath, byte[] data) in files)
        {
            string[] parts = rawPath.Trim('\\', '/').Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            DirNode cur = rootNode;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!cur.Subdirs.TryGetValue(parts[i], out DirNode? next))
                {
                    next = new DirNode(parts[i]);
                    cur.Subdirs[parts[i]] = next;
                }
                cur = next;
            }
            cur.Files[parts[^1]] = data;
        }

        // Assign sectors:
        // 0..15: System Area
        // 16: Primary Volume Descriptor
        // 17: Supplementary Volume Descriptor (Joliet UCS-2BE so mixed-case / long names like autounattend.xml work identically to oscdimg!)
        // 18: Volume Descriptor Set Terminator
        int nextSector = 19;

        var allDirs = new List<DirNode>();
        CollectDirs(rootNode, allDirs);

        foreach (DirNode dir in allDirs)
        {
            dir.IsoSector = nextSector++;
            dir.JolietSector = nextSector++;
        }

        var filePlacements = new List<(byte[] Data, int Sector)>();
        foreach (DirNode dir in allDirs)
        {
            foreach ((string name, byte[] data) in dir.Files)
            {
                int sec = nextSector;
                int sectorsNeeded = Math.Max(1, (data.Length + sectorSize - 1) / sectorSize);
                nextSector += sectorsNeeded;
                dir.FileSectors[name] = sec;
                filePlacements.Add((data, sec));
            }
        }

        byte[] image = new byte[nextSector * sectorSize];

        // Sector 16: PVD
        WriteVolumeDescriptor(image, 16, vdType: 1, rootSector: rootNode.IsoSector, rootLen: sectorSize, totalSectors: nextSector, isJoliet: false);
        // Sector 17: Joliet SVD (ESC '%/@' at 88..90)
        WriteVolumeDescriptor(image, 17, vdType: 2, rootSector: rootNode.JolietSector, rootLen: sectorSize, totalSectors: nextSector, isJoliet: true);
        // Sector 18: Terminator
        image[18 * sectorSize] = 255;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, (18 * sectorSize) + 1);
        image[(18 * sectorSize) + 6] = 1;

        foreach (DirNode dir in allDirs)
        {
            WriteDirectorySector(image, dir.IsoSector, dir, isJoliet: false);
            WriteDirectorySector(image, dir.JolietSector, dir, isJoliet: true);
        }

        foreach ((byte[] data, int sec) in filePlacements)
        {
            Buffer.BlockCopy(data, 0, image, sec * sectorSize, data.Length);
        }

        File.WriteAllBytes(outputIsoPath, image);
    }

    private static void CollectDirs(DirNode node, List<DirNode> list)
    {
        list.Add(node);
        foreach (DirNode child in node.Subdirs.Values)
        {
            child.Parent = node;
            CollectDirs(child, list);
        }
    }

    private static void WriteVolumeDescriptor(
        byte[] image,
        int sector,
        byte vdType,
        int rootSector,
        int rootLen,
        int totalSectors,
        bool isJoliet)
    {
        int off = sector * 2048;
        image[off] = vdType;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, off + 1);
        image[off + 6] = 1;

        if (isJoliet)
        {
            image[off + 88] = 0x25; // '%'
            image[off + 89] = 0x2F; // '/'
            image[off + 90] = 0x45; // 'E' (UCS-2 Level 3)
        }

        WriteBoth32(image, off + 80, (uint)totalSectors);
        WriteBoth16(image, off + 120, 1);
        WriteBoth16(image, off + 124, 1);
        WriteBoth16(image, off + 128, 2048);

        // 34-byte root directory record at offset 156
        int rOff = off + 156;
        image[rOff] = 34;
        WriteBoth32(image, rOff + 2, (uint)rootSector);
        WriteBoth32(image, rOff + 10, (uint)rootLen);
        image[rOff + 25] = 0x02; // Directory flag
        WriteBoth16(image, rOff + 28, 1);
        image[rOff + 32] = 1;
        image[rOff + 33] = 0;
    }

    private static void WriteDirectorySector(byte[] image, int sector, DirNode dir, bool isJoliet)
    {
        int pos = sector * 2048;
        int selfSec = isJoliet ? dir.JolietSector : dir.IsoSector;
        DirNode parent = dir.Parent ?? dir;
        int parentSec = isJoliet ? parent.JolietSector : parent.IsoSector;

        pos += WriteDirRecord(image, pos, new byte[] { 0 }, selfSec, 2048, isDir: true);
        pos += WriteDirRecord(image, pos, new byte[] { 1 }, parentSec, 2048, isDir: true);

        foreach ((string subName, DirNode subNode) in dir.Subdirs)
        {
            byte[] idBytes = isJoliet
                ? Encoding.BigEndianUnicode.GetBytes(subName)
                : Encoding.ASCII.GetBytes(subName.ToUpperInvariant());
            int childSec = isJoliet ? subNode.JolietSector : subNode.IsoSector;
            pos += WriteDirRecord(image, pos, idBytes, childSec, 2048, isDir: true);
        }

        foreach ((string fileName, byte[] data) in dir.Files)
        {
            string isoName = $"{fileName};1";
            byte[] idBytes = isJoliet
                ? Encoding.BigEndianUnicode.GetBytes(isoName)
                : Encoding.ASCII.GetBytes(isoName.ToUpperInvariant());
            pos += WriteDirRecord(image, pos, idBytes, dir.FileSectors[fileName], data.Length, isDir: false);
        }
    }

    private static int WriteDirRecord(byte[] image, int offset, byte[] idBytes, int extentLba, int dataLen, bool isDir)
    {
        int recLen = 33 + idBytes.Length;
        if ((recLen & 1) == 1) recLen++;

        image[offset] = (byte)recLen;
        WriteBoth32(image, offset + 2, (uint)extentLba);
        WriteBoth32(image, offset + 10, (uint)dataLen);
        image[offset + 25] = isDir ? (byte)0x02 : (byte)0x00;
        WriteBoth16(image, offset + 28, 1);
        image[offset + 32] = (byte)idBytes.Length;
        Buffer.BlockCopy(idBytes, 0, image, offset + 33, idBytes.Length);
        return recLen;
    }

    private static void WriteBoth16(byte[] buf, int off, ushort val)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), val);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(off + 2, 2), val);
    }

    private static void WriteBoth32(byte[] buf, int off, uint val)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), val);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(off + 4, 4), val);
    }

    private sealed class DirNode
    {
        public DirNode(string name) => Name = name;
        public string Name { get; }
        public DirNode? Parent { get; set; }
        public int IsoSector { get; set; }
        public int JolietSector { get; set; }
        public Dictionary<string, DirNode> Subdirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> FileSectors { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public int CallCount { get; private set; }

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("Direct C# ISO inspection must not invoke external processes.");
        }
    }
}
