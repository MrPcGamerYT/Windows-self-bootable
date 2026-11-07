using UsbFreeInstaller.Core.Services.Native;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class WinPeStartnetComposerTests
{
    [Theory]
    [InlineData("@echo off\r\nwpeinit\r\nX:\\sources\\setup.exe\r\n")]
    [InlineData("call wpeinit.exe /unattend:X:\\unattend.xml\r\nX:\\sources\\setup.exe\r\n")]
    [InlineData("X:\\sources\\setup.exe\r\n")]
    public void InitializesWinPe_ThenLoadsDrivers_BeforeLaunchingSetup(string original)
    {
        string output = WinPeStartnetComposer.AddStorageDriverLoading(original, includeLabConfig: true);
        int init = output.IndexOf("wpeinit", StringComparison.OrdinalIgnoreCase);
        int driver = output.IndexOf("drvload", StringComparison.OrdinalIgnoreCase);
        int setup = output.IndexOf("setup.exe", StringComparison.OrdinalIgnoreCase);

        Assert.True(init >= 0 && init < driver && driver < setup, output);
        Assert.Contains("BypassStorageCheck", output);
        Assert.Contains("SWBI-BootDrivers", output);
        Assert.Equal(1, output.Split("drvload", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void RebuildingTwice_DoesNotDuplicateDriverLoadingOrWpeinit()
    {
        string once = WinPeStartnetComposer.AddStorageDriverLoading("wpeinit\r\nsetup.exe\r\n", false);
        string twice = WinPeStartnetComposer.AddStorageDriverLoading(once, false);

        Assert.Equal(1, twice.Split("drvload", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, twice.Split("wpeinit", StringSplitOptions.None).Length - 1);
    }
}
