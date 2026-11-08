using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Native;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class WinPeShellIntegrationTests
{
    [Fact]
    public void StandardSetupShell_LoadsDriversBeforeRootSetupExecutable()
    {
        WithImage(image =>
        {
            File.WriteAllText(Path.Combine(image, "setup.exe"), "stub");
            WinPeShellIntegration.EnsureDriverLoaderRunsBeforeSetup(image, false, new SafeRedactingLogger());

            string system32 = Path.Combine(image, "Windows", "System32");
            string ini = File.ReadAllText(Path.Combine(system32, "winpeshl.ini"));
            string loader = File.ReadAllText(Path.Combine(system32, "swbi-load-drivers.cmd"));
            Assert.True(ini.IndexOf("swbi-load-drivers.cmd", StringComparison.OrdinalIgnoreCase) <
                        ini.IndexOf("%SYSTEMDRIVE%\\setup.exe", StringComparison.OrdinalIgnoreCase));
            Assert.True(loader.IndexOf("wpeinit", StringComparison.OrdinalIgnoreCase) <
                        loader.IndexOf("drvload", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Theory]
    [InlineData("[LaunchApp]\r\nAppPath = X:\\Tools\\custom.exe\r\n")]
    [InlineData("[LaunchApps]\r\nX:\\Tools\\custom.exe\r\n")]
    public void CustomWinPeShell_RetainsItsAppAfterDriverLoader(string original)
    {
        WithImage(image =>
        {
            string iniPath = Path.Combine(image, "Windows", "System32", "winpeshl.ini");
            File.WriteAllText(iniPath, original);
            WinPeShellIntegration.EnsureDriverLoaderRunsBeforeSetup(image, true, new SafeRedactingLogger());
            string ini = File.ReadAllText(iniPath);
            Assert.True(ini.IndexOf("swbi-load-drivers.cmd", StringComparison.OrdinalIgnoreCase) <
                        ini.IndexOf("custom.exe", StringComparison.OrdinalIgnoreCase), ini);
            Assert.Contains("X:\\Tools\\custom.exe", ini);

            WinPeShellIntegration.EnsureDriverLoaderRunsBeforeSetup(image, true, new SafeRedactingLogger());
            Assert.Equal(1, File.ReadAllText(iniPath).Split("swbi-load-drivers.cmd", StringSplitOptions.None).Length - 1);
        });
    }

    [Fact]
    public void UnknownShell_RefusesToReplaceSetupOrWritePartialIni()
    {
        WithImage(image =>
        {
            string system32 = Path.Combine(image, "Windows", "System32");
            string iniPath = Path.Combine(system32, "winpeshl.ini");
            File.WriteAllText(iniPath, "[UnknownShell]\r\nLauncher=custom.exe\r\n");
            Assert.Throws<InvalidDataException>(() => WinPeShellIntegration.EnsureDriverLoaderRunsBeforeSetup(
                image, false, new SafeRedactingLogger()));
            Assert.Equal("[UnknownShell]\r\nLauncher=custom.exe\r\n", File.ReadAllText(iniPath));
            Assert.False(File.Exists(Path.Combine(system32, "swbi-load-drivers.cmd")));
        });
    }

    private static void WithImage(Action<string> test)
    {
        string image = Path.Combine(Path.GetTempPath(), $"swbi-pe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(image, "Windows", "System32"));
        try { test(image); }
        finally { Directory.Delete(image, recursive: true); }
    }
}
