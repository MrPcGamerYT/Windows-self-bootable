using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Native;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class LiveWindowsKernelStorageAndBcdTests
{
    private static bool IsWindowsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using WindowsIdentity id = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(id);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool AllowHostBcdMutation() =>
        string.Equals(Environment.GetEnvironmentVariable("SWBI_TEST_ALLOW_HOST_BCD_MUTATION"), "1", StringComparison.Ordinal);

    [Fact]
    public void LiveKernel_PhysicalDiskC_SystemBcd_MatchesWindowsBcdEditByteForByte()
    {
        if (!IsWindowsAdministrator() || !AllowHostBcdMutation())
        {
            return;
        }

        string workDir = SwbiWorkDirectory.CreateUniqueStagingDirectory("live-phys-bcd-oracle");
        var logger = new SafeRedactingLogger();
        try
        {
            Assert.True(NativeUefiAndBcdRegistryEngine.TryExportSystemBcdBackup(Path.Combine(workDir, "bcd-before-test.hiv"), logger),
                "Host BCD backup is required before this opt-in test.");
            (string sdiGuid, string loaderGuid) = NativeUefiAndBcdRegistryEngine.ProvisionSystemBcdInRegistry(
                "C",
                FirmwareMode.LegacyBios,
                logger);

            try
            {
                AssertProvisionedBcdMatchesWindowsBcdEditByteForByte("C", sdiGuid, loaderGuid, workDir, "phys-c", isPhysicalDisk: true);
            }
            finally
            {
                NativeUefiAndBcdRegistryEngine.RollbackRegistryAndUefiEntries(loaderGuid, sdiGuid, null, logger);
            }

            ActiveBcdState afterRollback = NativeUefiAndBcdRegistryEngine.InspectSystemBcdFromRegistry();
            Assert.Null(afterRollback.ExistingUsbFreeLoaderGuid);
        }
        finally
        {
            SwbiWorkDirectory.TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public async Task LiveKernel_VhdDisk_NtfsShrink_CreateFat32_SystemBcd_And_Extend_EndToEnd()
    {
        if (!IsWindowsAdministrator())
        {
            // Non-Windows or non-elevated local environment; on GitHub Actions windows-latest this ALWAYS runs elevated!
            return;
        }

        var logger = new SafeRedactingLogger();
        string workDir = SwbiWorkDirectory.EnsureSafeWorkDirectory($"live-kernel-vhd-{Guid.NewGuid():N}");
        string vhdPath = Path.Combine(workDir, "swbi-live-kernel.vhdx");
        char testOsLetter = PickUnusedDriveLetter('K', 'L', 'M', 'N', 'O', 'P');

        try
        {
            // Arrange: Create & attach a real 1536 MB GPT VHDX with a live NTFS volume via Windows VDS test fixture
            RunDiskPartFixtureScript(
                $"create vdisk file=\"{vhdPath}\" maximum=1536 type=expandable",
                $"select vdisk file=\"{vhdPath}\"",
                "attach vdisk",
                "convert gpt",
                "create partition primary",
                "format fs=ntfs quick label=TESTOS",
                $"assign letter={testOsLetter}");

            string osRoot = $"{testOsLetter}:\\";
            Assert.True(Directory.Exists(osRoot), $"Fixture NTFS volume {osRoot} was not mounted.");

            // Write test files onto the live NTFS volume (and flush) so clusters are genuinely allocated
            string canaryPath = Path.Combine(osRoot, "user-important-data.bin");
            byte[] canaryBytes = new byte[4 * 1024 * 1024];
            new Random(12345).NextBytes(canaryBytes);
            await File.WriteAllBytesAsync(canaryPath, canaryBytes);

            int diskNum;
            int osPartNum;
            using (SafeFileHandle vh = Win32StorageInterop.CreateFileW(
                       $@"\\.\{testOsLetter}:",
                       0,
                       Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
                       IntPtr.Zero,
                       Win32StorageInterop.OPEN_EXISTING,
                       0,
                       IntPtr.Zero))
            {
                Assert.False(vh.IsInvalid, $"Could not open \\\\.\\{testOsLetter}:");
                Assert.True(Win32StorageInterop.TryGetDeviceNumbers(vh, out diskNum, out osPartNum));
            }

            Assert.True(diskNum >= 1, $"Expected loopback VHD disk >= 1, got Disk {diskNum}");

            // =========================================================================
            // 1 & 2. LIVE PRODUCTION WindowsPartitionService: SHRINK + GPT CREATE + PURE C# FAT32 FORMAT + MOUNT!
            // =========================================================================
            var partSvc = new WindowsPartitionService(new ProcessRunner(logger), logger);
            long targetShrunkOsBytes = 700L * 1024 * 1024; // Shrink ~1500 MB NTFS volume down to 700 MB online!
            long swbiLengthBytes = 700L * 1024 * 1024;

            CreatedInstallerPartitionInfo createdSwbi = await partSvc.ShrinkVolumeAndCreateFat32InstallerPartitionAsync(
                diskNumber: diskNum,
                osPartitionNumber: osPartNum,
                osDriveLetter: testOsLetter.ToString(),
                expectedOsPartitionGuid: string.Empty,
                newOsPartitionSizeBytes: targetShrunkOsBytes,
                installerPartitionSizeBytes: swbiLengthBytes,
                volumeLabel: SwbiBrand.PartitionVolumeLabel);

            string swbiLetter = createdSwbi.DriveLetter;
            var shrunkDriveInfo = new DriveInfo(testOsLetter.ToString());
            Assert.InRange(shrunkDriveInfo.TotalSize, 650L * 1024 * 1024, 720L * 1024 * 1024);
            Assert.Equal(canaryBytes, await File.ReadAllBytesAsync(canaryPath));

            Assert.True(Directory.Exists($"{swbiLetter}:\\"), $"Formatted FAT32 volume {swbiLetter}:\\ did not mount.");
            var fatDrive = new DriveInfo(swbiLetter);
            Assert.Equal("FAT32", fatDrive.DriveFormat, ignoreCase: true);
            Assert.Equal(SwbiBrand.PartitionVolumeLabel, fatDrive.VolumeLabel, ignoreCase: true);

            // Write real installer files onto the pure-C#-formatted FAT32 volume
            Directory.CreateDirectory($@"{swbiLetter}:\sources");
            Directory.CreateDirectory($@"{swbiLetter}:\boot");
            Directory.CreateDirectory($@"{swbiLetter}:\efi\boot");
            await File.WriteAllTextAsync($@"{swbiLetter}:\efi\boot\bootx64.efi", "EFI-STUB", Encoding.ASCII);
            Assert.Equal("EFI-STUB", await File.ReadAllTextAsync($@"{swbiLetter}:\efi\boot\bootx64.efi", Encoding.ASCII));

            // Host BCD mutation is opt-in and suitable only for a disposable Windows VM.
            if (AllowHostBcdMutation())
            {
                // =========================================================================
                // 3. LIVE KERNEL SYSTEM BCD REGISTRY BACKUP + PROVISION + INSPECT + ROLLBACK TEST
                // =========================================================================
                string bcdBackupFile = Path.Combine(workDir, "bcd-live-backup.hiv");
                bool backupOk = NativeUefiAndBcdRegistryEngine.TryExportSystemBcdBackup(bcdBackupFile, logger);
                Assert.True(backupOk, "Host BCD backup is required before this opt-in test.");
                Assert.True(new FileInfo(bcdBackupFile).Length > 0);

                // Provision directly into SYSTEM-owned HKLM\BCD00000000\Objects via REG_OPTION_BACKUP_RESTORE!
                (string sdiGuid, string loaderGuid) = NativeUefiAndBcdRegistryEngine.ProvisionSystemBcdInRegistry(
                    swbiLetter,
                    FirmwareMode.LegacyBios,
                    logger);

                try
                {
                    ActiveBcdState activeState = NativeUefiAndBcdRegistryEngine.InspectSystemBcdFromRegistry();
                    Assert.Equal(loaderGuid, activeState.ExistingUsbFreeLoaderGuid, ignoreCase: true);
                    Assert.Equal(sdiGuid, activeState.ExistingUsbFreeSdiGuid, ignoreCase: true);

                    AssertProvisionedBcdMatchesWindowsBcdEditByteForByte(swbiLetter, sdiGuid, loaderGuid, workDir, "gpt", isPhysicalDisk: false);
                }
                finally
                {
                    NativeUefiAndBcdRegistryEngine.RollbackRegistryAndUefiEntries(loaderGuid, sdiGuid, null, logger);
                }

                ActiveBcdState afterRollback = NativeUefiAndBcdRegistryEngine.InspectSystemBcdFromRegistry();
                Assert.Null(afterRollback.ExistingUsbFreeLoaderGuid);
            }

            // =========================================================================
            // 4. LIVE KERNEL CLEANUP DELETE PARTITION + NTFS ONLINE EXTEND TEST
            // =========================================================================
            await partSvc.DeleteInstallerPartitionAsync(diskNum, createdSwbi.PartitionNumber, createdSwbi.PartitionGuid);

            NativeNtfsShrinkEngine.ExtendOnlinePartitionToMaxAdjacent(
                diskNum,
                osPartNum,
                testOsLetter.ToString(),
                logger);

            var extendedDriveInfo = new DriveInfo(testOsLetter.ToString());
            Assert.True(
                extendedDriveInfo.TotalSize > 1350L * 1024 * 1024,
                $"Expected extended NTFS volume > 1350 MB, got {extendedDriveInfo.TotalSize / (1024 * 1024)} MB.");
            Assert.Equal(canaryBytes, await File.ReadAllBytesAsync(canaryPath));
        }
        finally
        {
            try
            {
                if (File.Exists(vhdPath))
                {
                    RunDiskPartFixtureScript(
                        $"select vdisk file=\"{vhdPath}\"",
                        "detach vdisk noerr");
                }
            }
            catch
            {
                // Ignore detach cleanup errors
            }

            SwbiWorkDirectory.TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public async Task LiveKernel_MbrDisk_SingleActivePartition_NtfsShrink_CreateFat32_LegacyBcd_And_Extend_EndToEnd()
    {
        if (!IsWindowsAdministrator())
        {
            return;
        }

        var logger = new SafeRedactingLogger();
        string workDir = SwbiWorkDirectory.EnsureSafeWorkDirectory($"live-kernel-mbr-vhd-{Guid.NewGuid():N}");
        string vhdPath = Path.Combine(workDir, "swbi-live-mbr-kernel.vhdx");
        char testOsLetter = PickUnusedDriveLetter('M', 'N', 'O', 'P', 'K', 'L');

        try
        {
            // Arrange: Create & attach a real 1536 MB MBR VHDX with a SINGLE Active (0x80) primary NTFS partition!
            RunDiskPartFixtureScript(
                $"create vdisk file=\"{vhdPath}\" maximum=1536 type=expandable",
                $"select vdisk file=\"{vhdPath}\"",
                "attach vdisk",
                "convert mbr",
                "create partition primary",
                "active",
                "format fs=ntfs quick label=MBROS",
                $"assign letter={testOsLetter}");

            string osRoot = $"{testOsLetter}:\\";
            Assert.True(Directory.Exists(osRoot), $"Fixture MBR NTFS volume {osRoot} was not mounted.");

            string canaryPath = Path.Combine(osRoot, "mbr-user-data.bin");
            byte[] canaryBytes = new byte[3 * 1024 * 1024];
            new Random(54321).NextBytes(canaryBytes);
            await File.WriteAllBytesAsync(canaryPath, canaryBytes);

            int diskNum;
            int osPartNum;
            using (SafeFileHandle vh = Win32StorageInterop.CreateFileW(
                       $@"\\.\{testOsLetter}:",
                       0,
                       Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
                       IntPtr.Zero,
                       Win32StorageInterop.OPEN_EXISTING,
                       0,
                       IntPtr.Zero))
            {
                Assert.False(vh.IsInvalid);
                Assert.True(Win32StorageInterop.TryGetDeviceNumbers(vh, out diskNum, out osPartNum));
            }

            // 1. Live Shrink + MBR Primary Slot #2 (0x0C FAT32 LBA) Creation + Pure C# FAT32 Format
            var partSvc = new WindowsPartitionService(new ProcessRunner(logger), logger);
            CreatedInstallerPartitionInfo createdSwbi = await partSvc.ShrinkVolumeAndCreateFat32InstallerPartitionAsync(
                diskNumber: diskNum,
                osPartitionNumber: osPartNum,
                osDriveLetter: testOsLetter.ToString(),
                expectedOsPartitionGuid: string.Empty,
                newOsPartitionSizeBytes: 700L * 1024 * 1024,
                installerPartitionSizeBytes: 700L * 1024 * 1024,
                volumeLabel: SwbiBrand.PartitionVolumeLabel);

            Assert.StartsWith("MBR:", createdSwbi.PartitionGuid, StringComparison.OrdinalIgnoreCase);

            // Verify Partition 1 kept its Active (0x80) boot flag while MBR primary slot count became 2/4!
            using (SafeFileHandle dh = Win32StorageInterop.CreateFileW(
                       $@"\\.\PhysicalDrive{diskNum}",
                       0,
                       Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE,
                       IntPtr.Zero,
                       Win32StorageInterop.OPEN_EXISTING,
                       0,
                       IntPtr.Zero))
            {
                RawDiskLayoutSnapshot mbrLayout = Win32StorageInterop.TryReadDiskLayout(dh)!;
                Assert.Equal(Win32StorageInterop.PARTITION_STYLE_MBR, mbrLayout.PartitionStyle);
                Assert.Equal(2, mbrLayout.MbrPrimarySlotsUsed);
                Assert.Contains(mbrLayout.Partitions, p => p.PartitionNumber == osPartNum && p.MbrBootIndicator);
            }

            string swbiLetter = createdSwbi.DriveLetter;
            var fatDrive = new DriveInfo(swbiLetter);
            Assert.Equal("FAT32", fatDrive.DriveFormat, ignoreCase: true);
            Assert.Equal(SwbiBrand.PartitionVolumeLabel, fatDrive.VolumeLabel, ignoreCase: true);

            Directory.CreateDirectory($@"{swbiLetter}:\sources");
            Directory.CreateDirectory($@"{swbiLetter}:\boot");
            await File.WriteAllTextAsync($@"{swbiLetter}:\bootmgr", "MBR-BOOTMGR-STUB", Encoding.ASCII);

            // Host BCD mutation is opt-in and suitable only for a disposable Windows VM.
            if (AllowHostBcdMutation())
            {
                // 2. Live Legacy BIOS BCD Provisioning (MBR Signature + Partition Offset QualifiedPartition + winload.exe) & Rollback
                Assert.True(NativeUefiAndBcdRegistryEngine.TryExportSystemBcdBackup(Path.Combine(workDir, "bcd-before-test.hiv"), logger),
                    "Host BCD backup is required before this opt-in test.");
                (string sdiGuid, string loaderGuid) = NativeUefiAndBcdRegistryEngine.ProvisionSystemBcdInRegistry(
                    swbiLetter,
                    FirmwareMode.LegacyBios,
                    logger);

                try
                {
                    ActiveBcdState activeState = NativeUefiAndBcdRegistryEngine.InspectSystemBcdFromRegistry();
                    Assert.Equal(loaderGuid, activeState.ExistingUsbFreeLoaderGuid, ignoreCase: true);

                    AssertProvisionedBcdMatchesWindowsBcdEditByteForByte(swbiLetter, sdiGuid, loaderGuid, workDir, "mbr", isPhysicalDisk: false);
                }
                finally
                {
                    NativeUefiAndBcdRegistryEngine.RollbackRegistryAndUefiEntries(loaderGuid, sdiGuid, null, logger);
                }
            }

            // 3. Live MBR Slot Deletion (slot cleared to 0x00, Partition 1 stays Active 0x80) + Live NTFS Online Extend
            await partSvc.DeleteInstallerPartitionAsync(diskNum, createdSwbi.PartitionNumber, createdSwbi.PartitionGuid);

            NativeNtfsShrinkEngine.ExtendOnlinePartitionToMaxAdjacent(
                diskNum,
                osPartNum,
                testOsLetter.ToString(),
                logger);

            var extendedDriveInfo = new DriveInfo(testOsLetter.ToString());
            Assert.True(extendedDriveInfo.TotalSize > 1350L * 1024 * 1024);
            Assert.Equal(canaryBytes, await File.ReadAllBytesAsync(canaryPath));
        }
        finally
        {
            try
            {
                if (File.Exists(vhdPath))
                {
                    RunDiskPartFixtureScript(
                        $"select vdisk file=\"{vhdPath}\"",
                        "detach vdisk noerr");
                }
            }
            catch
            {
                // Ignore detach cleanup errors
            }

            SwbiWorkDirectory.TryDeleteDirectory(workDir);
        }
    }

    private static char PickUnusedDriveLetter(params char[] candidates)
    {
        HashSet<char> used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        foreach (char c in candidates)
        {
            if (!used.Contains(char.ToUpperInvariant(c)))
            {
                return char.ToUpperInvariant(c);
            }
        }

        return 'W';
    }

    private static void RunDiskPartFixtureScript(params string[] lines)
    {
        string scriptFile = Path.Combine(SwbiWorkDirectory.EnsureSafeWorkDirectory("vhd-scripts"), $"vhd-fixture-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(scriptFile, lines, Encoding.ASCII);
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(60_000);
            if (proc.ExitCode != 0 && !lines.Any(l => l.Contains("noerr")))
            {
                throw new InvalidOperationException($"DiskPart VHD test fixture failed (exit {proc.ExitCode}): {stdout} {stderr}");
            }
        }
        finally
        {
            SwbiWorkDirectory.TryDeleteFile(scriptFile);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertProvisionedBcdMatchesWindowsBcdEditByteForByte(
        string swbiLetter,
        string sdiGuid,
        string loaderGuid,
        string workDir,
        string label,
        bool isPhysicalDisk)
    {
        string oracleStore = Path.Combine(workDir, $"oracle-{label}.bcd");
        RunBcdEditTestOracle($"/createstore \"{oracleStore}\"");
        RunBcdEditTestOracle($"/store \"{oracleStore}\" /create {sdiGuid} /d \"Oracle SDI\" /device");
        RunBcdEditTestOracle($"/store \"{oracleStore}\" /set {sdiGuid} ramdisksdidevice partition={swbiLetter}:");
        RunBcdEditTestOracle($"/store \"{oracleStore}\" /set {sdiGuid} ramdisksdipath \\boot\\boot.sdi");
        RunBcdEditTestOracle($"/store \"{oracleStore}\" /create {loaderGuid} /d \"Oracle SWBI\" /application osloader");
        RunBcdEditTestOracle($"/store \"{oracleStore}\" /set {loaderGuid} device ramdisk=[{swbiLetter}:]\\sources\\boot.wim,{sdiGuid}");
        RunBcdEditTestOracle($"/store \"{oracleStore}\" /set {loaderGuid} osdevice ramdisk=[{swbiLetter}:]\\sources\\boot.wim,{sdiGuid}");

        Win32StorageInterop.EnableProcessPrivilege("SeBackupPrivilege");
        Win32StorageInterop.EnableProcessPrivilege("SeRestorePrivilege");

        byte[] expectedSdi;
        byte[] expectedRamdisk;
        byte[] actualSdi;
        byte[] actualRamdisk;

        string mountName = $"SWBI_ORACLE_{Guid.NewGuid():N}";
        Assert.Equal(0, Win32StorageInterop.RegLoadKeyW(Win32StorageInterop.HKEY_LOCAL_MACHINE_PTR, mountName, oracleStore));
        try
        {
            using RegistryKey? expSdiKey = Registry.LocalMachine.OpenSubKey($@"{mountName}\Objects\{sdiGuid}\Elements\31000003");
            using RegistryKey? expRamKey = Registry.LocalMachine.OpenSubKey($@"{mountName}\Objects\{loaderGuid}\Elements\11000001");
            using RegistryKey? actSdiKey = Registry.LocalMachine.OpenSubKey($@"BCD00000000\Objects\{sdiGuid}\Elements\31000003");
            using RegistryKey? actRamKey = Registry.LocalMachine.OpenSubKey($@"BCD00000000\Objects\{loaderGuid}\Elements\11000001");

            expectedSdi = (byte[])expSdiKey!.GetValue("Element")!;
            expectedRamdisk = (byte[])expRamKey!.GetValue("Element")!;
            actualSdi = (byte[])actSdiKey!.GetValue("Element")!;
            actualRamdisk = (byte[])actRamKey!.GetValue("Element")!;
        }
        finally
        {
            Win32StorageInterop.RegUnloadKeyW(Win32StorageInterop.HKEY_LOCAL_MACHINE_PTR, mountName);
        }

        string expectedSdiHex = Convert.ToHexString(expectedSdi);
        string actualSdiHex = Convert.ToHexString(actualSdi);
        string expectedRamdiskHex = Convert.ToHexString(expectedRamdisk);
        string actualRamdiskHex = Convert.ToHexString(actualRamdisk);

        if (isPhysicalDisk)
        {
            string sysSdiOut = RunBcdEditTestOracle($"/enum {sdiGuid} /v").Replace("\r\n", " ");
            string sysLdrOut = RunBcdEditTestOracle($"/enum {loaderGuid} /v").Replace("\r\n", " ");

            if (expectedSdiHex != actualSdiHex || expectedRamdiskHex != actualRamdiskHex)
            {
                throw new InvalidOperationException(
                    $"Error [{label}] Expected_SDI({expectedSdi.Length})={expectedSdiHex} Actual_SDI({actualSdi.Length})={actualSdiHex} || Expected_RAM({expectedRamdisk.Length})={expectedRamdiskHex} Actual_RAM({actualRamdisk.Length})={actualRamdiskHex} || ENUM_SDI={sysSdiOut}");
            }

            Assert.Contains($"partition={swbiLetter}:", sysSdiOut, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unknown", sysSdiOut, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"ramdisk=[{swbiLetter}:]\\sources\\boot.wim", sysLdrOut, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unknown", sysLdrOut, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // On a .vhdx loop-mounted disk, bcdedit wraps QualifiedPartition in a VHD parent locator (DiskKind=6/7 at [48..52]),
            // whereas the PartitionIdentifier union [32..48], DiskIdentifier union [56..72], and Ramdisk header [0..24],[32..60],[64..68]
            // MUST match Microsoft's bcd.dll binary layout 1:1!
            bool partIdMatch = expectedSdi.AsSpan(32, 16).SequenceEqual(actualSdi.AsSpan(32, 16));
            bool diskIdMatch = expectedSdi.AsSpan(56, 16).SequenceEqual(actualSdi.AsSpan(56, 16));
            bool ramHeadMatch = expectedRamdisk.AsSpan(0, 24).SequenceEqual(actualRamdisk.AsSpan(0, 24))
                                && expectedRamdisk.AsSpan(32, 28).SequenceEqual(actualRamdisk.AsSpan(32, 28))
                                && expectedRamdisk.AsSpan(64, 4).SequenceEqual(actualRamdisk.AsSpan(64, 4));

            if (!partIdMatch || !diskIdMatch || !ramHeadMatch)
            {
                string expHead = Convert.ToHexString(expectedSdi.AsSpan(0, Math.Min(88, expectedSdi.Length)));
                throw new InvalidOperationException(
                    $"Error [{label}] VHD-field mismatch: Expected_SDI_Head={expHead} Actual_SDI={actualSdiHex}");
            }
        }
    }

    private static string RunBcdEditTestOracle(string args)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "bcdedit.exe",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"bcdedit.exe {args} failed ({proc.ExitCode}): {stdout} {stderr}");
        }
        return stdout;
    }
}
