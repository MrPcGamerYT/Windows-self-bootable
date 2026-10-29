using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Simulation;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class SwbiBrandAndBootChooserTests
{
    [Fact]
    public void PartitionVolumeLabel_FitsStrictFat32ElevenCharLimit()
    {
        Assert.Equal("SWBI-SETUP", SwbiBrand.PartitionVolumeLabel);
        Assert.True(SwbiBrand.PartitionVolumeLabel.Length <= 11);
        Assert.All(SwbiBrand.PartitionVolumeLabel, c => Assert.True(c < 128));
    }

    [Fact]
    public void EmbeddedSwbiPartitionIcon_IsBundledInCoreAssembly()
    {
        using Stream? iconStream = typeof(SwbiBrand).Assembly
            .GetManifestResourceStream("UsbFreeInstaller.Core.Resources.swbi.ico");

        Assert.NotNull(iconStream);
        Assert.True(iconStream!.Length > 10_000);
    }

    [Fact]
    public void FindSwbiBcdEntries_DistinguishesWindowsBootLoader_UefiPendriveChooser_AndSdiDevice()
    {
        const string sampleBcdEnumAll = """
Firmware Application (101fffff)
-------------------------------
identifier              {aaaaaaaa-1111-4444-8888-000000000010}
device                  partition=W:
path                    \efi\boot\bootx64.efi
description             SWBI - Self Windows Bootable Installer

Windows Boot Loader
-------------------
identifier              {bbbbbbbb-2222-4444-8888-000000000020}
device                  ramdisk=[W:]\sources\boot.wim,{cccccccc-3333-4444-8888-000000000030}
path                    \Windows\system32\boot\winload.efi
description             SWBI - Self Windows Bootable Installer
osdevice                ramdisk=[W:]\sources\boot.wim,{cccccccc-3333-4444-8888-000000000030}
systemroot              \Windows
winpe                   Yes

Device options
--------------
identifier              {cccccccc-3333-4444-8888-000000000030}
description             SWBI - Self Windows Bootable Installer SDI
ramdisksdidevice        partition=W:
ramdisksdipath          \boot\boot.sdi
""";

        (string? loaderGuid, string? sdiGuid, string? fwGuid) =
            WindowsBcdBootService.FindSwbiBcdEntries(sampleBcdEnumAll);

        Assert.Equal("{bbbbbbbb-2222-4444-8888-000000000020}", loaderGuid);
        Assert.Equal("{cccccccc-3333-4444-8888-000000000030}", sdiGuid);
        Assert.Equal("{aaaaaaaa-1111-4444-8888-000000000010}", fwGuid);
    }

    [Fact]
    public void SetupGuideGenerator_SurfacesSwbiAcrossPartitionBootAndDriverChoosers()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        PartitionSizingPlan plan = new ReadinessEvaluator(new PartitionSizingCalculator())
            .Evaluate(scenario.Environment, scenario.SampleIso)
            .SizingPlan!;

        var generator = new SetupGuideGenerator();
        SetupPartitionGuide guide = generator.Generate(scenario.Environment, plan);
        string plainText = generator.FormatPlainTextCheatSheet(guide);

        SetupPartitionRow installerRow = Assert.Single(guide.Rows, r => r.Action == SetupPartitionAction.PreserveInstallerSource);
        Assert.Contains("SWBI-SETUP", installerRow.SetupName);
        Assert.Contains("SWBI — Self Windows Bootable Installer", installerRow.Explanation);

        Assert.Contains("SWBI - Self Windows Bootable Installer", plainText);
        Assert.Contains("SWBI-SETUP", plainText);
        Assert.Contains(SwbiBrand.DriverChooserFolderName, plainText);
    }
    [Fact]
    public void SetupGuide_DescribesOnlyTheDetectedOneTimeBootPath()
    {
        SimulationScenario scenario = SimulatedEnvironmentProvider.CreateHealthySingleDriveScenario();
        PartitionSizingPlan plan = new ReadinessEvaluator(new PartitionSizingCalculator())
            .Evaluate(scenario.Environment, scenario.SampleIso).SizingPlan!;
        var generator = new SetupGuideGenerator();
        string uefi = generator.Generate(scenario.Environment with { Firmware = FirmwareMode.Uefi }, plan).CriticalRules[0];
        string legacy = generator.Generate(scenario.Environment with { Firmware = FirmwareMode.LegacyBios }, plan).CriticalRules[0];
        Assert.Contains("BootNext", uefi);
        Assert.DoesNotContain("Windows Boot Manager", uefi);
        Assert.Contains("Windows Boot Manager", legacy);
        Assert.DoesNotContain("BootNext", legacy);
    }
}
