using System.Runtime.InteropServices;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Native;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

/// <summary>
/// Exercises the actual Windows wimgapi capture/export/rebuild path without modifying
/// any host disk or BCD. This is not a WinPE boot or hardware-driver compatibility test.
/// </summary>
public sealed class BootWimRoundTripWindowsTests
{
    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr WIMCreateFile(string path, uint access, uint disposition, uint flags,
        uint compression, out uint creationResult);

    [DllImport("wimgapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr WIMCaptureImage(IntPtr wim, string directory, uint flags);

    [DllImport("wimgapi.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool WIMSetBootImage(IntPtr wim, uint imageIndex);

    [DllImport("wimgapi.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint WIMGetImageCount(IntPtr wim);

    [DllImport("wimgapi.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool WIMCloseHandle(IntPtr handle);

    [Fact]
    public void RebuildsTwoImageWim_WithWinPeShellHookAndActiveDriverInf()
    {
        if (!OperatingSystem.IsWindows() || !NativeWimApi.IsWimgapiAvailable()) return;

        string root = SwbiWorkDirectory.CreateUniqueStagingDirectory("bootwim-roundtrip");
        try
        {
            string pe = Path.Combine(root, "pe");
            Directory.CreateDirectory(Path.Combine(pe, "Windows", "System32"));
            File.WriteAllText(Path.Combine(pe, "Windows", "System32", "startnet.cmd"), "wpeinit\r\n");
            string bootWim = Path.Combine(root, "boot.wim");
            NativeWimApi.CaptureDirectoryToWim(pe, bootWim); // image 1: base WinPE

            string setupImage = Path.Combine(root, "setupImage");
            Directory.CreateDirectory(Path.Combine(setupImage, "Windows", "System32"));
            File.WriteAllText(Path.Combine(setupImage, "Windows", "System32", "startnet.cmd"), "wpeinit\r\n");
            byte[] payload = new byte[2 * 1024 * 1024];
            new Random(20261107).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(setupImage, "setup.exe"), payload);
            IntPtr wim = WIMCreateFile(bootWim, 0xC0000000, 3, 0, 0, out _);
            Assert.NotEqual(IntPtr.Zero, wim);
            try
            {
                IntPtr secondImage = WIMCaptureImage(wim, setupImage, 0);
                Assert.NotEqual(IntPtr.Zero, secondImage);
                WIMCloseHandle(secondImage);
                Assert.Equal(2u, WIMGetImageCount(wim));
                Assert.True(WIMSetBootImage(wim, 2));
            }
            finally { WIMCloseHandle(wim); }
            Assert.True(new FileInfo(bootWim).Length > 1024 * 1024);

            string driverDir = Path.Combine(root, "drivers");
            string package = Path.Combine(driverDir, "iastorvd.inf_amd64_test");
            Directory.CreateDirectory(package);
            string driverInf = Path.Combine(package, "iaStorVD.inf");
            File.WriteAllText(driverInf, "[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nClass=SCSIAdapter\r\n");
            File.WriteAllBytes(Path.Combine(package, "iaStorVD.sys"), new byte[] { 0x4D, 0x5A, 0x90 });

            var logger = new SafeRedactingLogger();
            bool customized = NativeWimApi.CustomizeBootWimInProcess(bootWim,
                applyWin11HardwareBypass: false, storageDriversSourceDir: driverDir,
                logger: logger, requiredDriverInfPath: driverInf);
            Assert.True(customized, string.Join("\n", logger.GetEntriesSnapshot().Select(e => e.Message)));
            Assert.True(File.Exists(bootWim + ".swbi-backup"));
            Assert.True(NativeWimApi.VerifyWimOrSwmIntegrity(bootWim, out string detail), detail);
        }
        finally { SwbiWorkDirectory.TryDeleteDirectory(root); }
    }
}
