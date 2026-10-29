using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

/// <summary>
/// Regression tests for the two "silent damage" defects fixed in this pass:
///   1. ISO-supplied paths escaping the installer partition (SafeRelativePath).
///   2. Setup-guide generation aborting a completed preparation (SetupGuideGenerator fallbacks).
/// </summary>
public sealed class PathAndGuideSafetyTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "swbi-path-root"));

    [Theory]
    [InlineData(@"sources")]
    [InlineData(@"sources\install.wim")]
    [InlineData(@"efi\microsoft\boot\bcd")]
    [InlineData(@"x64\sources\boot.wim")]
    [InlineData(@"boot\boot.sdi")]
    [InlineData(@"sources\$OEM$\$$\Setup\Scripts\setupcomplete.cmd")]
    [InlineData(@"support\log files\setupact.log")]
    public void TryResolveUnder_AcceptsNormalWindowsIsoPaths(string relativePath)
    {
        Assert.True(SafeRelativePath.TryResolveUnder(Root, relativePath, out string full, out string error), error);
        Assert.StartsWith(Root, full, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(relativePath.Replace('/', '\\').Split('\\').Last(), full, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"..\..\..\Windows\System32\ntoskrnl.exe")]
    [InlineData(@"sources\..\..\..\Windows\System32\config\SAM")]
    [InlineData(@"C:\Windows\System32\ntoskrnl.exe")]
    [InlineData(@"C:relative.txt")]
    [InlineData(@"\\server\share\payload.exe")]
    [InlineData(@"sources\file.txt:stream")]
    [InlineData(@"sources\CON")]
    [InlineData(@"sources\nul.txt")]
    [InlineData(@"sources\COM1.dat")]
    public void TryResolveUnder_RejectsAnythingThatEscapesThePartition(string relativePath)
    {
        Assert.False(SafeRelativePath.TryResolveUnder(Root, relativePath, out string full, out string error));
        Assert.Equal(string.Empty, full);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryResolveUnder_RejectsPathThatResolvesToTheRootItself()
    {
        Assert.False(SafeRelativePath.TryResolveUnder(Root, @"sources\..", out _, out _));
        Assert.False(SafeRelativePath.TryResolveUnder(Root, ".", out _, out _));
    }

    [Theory]
    [InlineData(@"..\payload.exe", true)]
    [InlineData(@"sources\..\payload.exe", true)]
    [InlineData(@"sources/payload.exe", false)]
    [InlineData(@"sources\payload.exe", false)]
    [InlineData("", false)]
    public void ContainsTraversalSegment_DetectsTraversal(string path, bool expected)
    {
        Assert.Equal(expected, SafeRelativePath.ContainsTraversalSegment(path));
    }

    [Fact]
    public void SetupGuide_DoesNotThrowAndProvidesLabelBasedRules_WhenTargetDiskIsMissing()
    {
        var generator = new SetupGuideGenerator();
        SystemEnvironmentSnapshot env = new()
        {
            IsElevated = true,
            Firmware = FirmwareMode.Uefi,
            SecureBootEnabled = true,
            HostArchitecture = "x64",
            SystemDriveLetter = "C",
            TargetDiskNumber = 7,
            TargetOsPartitionNumber = 3,
            AllDisks = Array.Empty<DiskSnapshot>()
        };

        SetupPartitionGuide guide = generator.Generate(env, BuildSizingPlan());

        Assert.Empty(guide.Rows);
        Assert.NotEmpty(guide.CriticalRules);
        Assert.Contains(guide.CriticalRules, rule => rule.Contains(SwbiBrand.PartitionVolumeLabel, StringComparison.Ordinal));
        Assert.DoesNotContain(guide.CriticalRules, rule => rule.Contains("Partition 3:", StringComparison.Ordinal));
    }

    [Fact]
    public void SetupGuide_DoesNotThrow_WhenTargetPartitionIsNotInTheDiskSnapshot()
    {
        var generator = new SetupGuideGenerator();
        SystemEnvironmentSnapshot env = new()
        {
            IsElevated = true,
            Firmware = FirmwareMode.LegacyBios,
            SecureBootEnabled = false,
            HostArchitecture = "x64",
            SystemDriveLetter = "C",
            TargetDiskNumber = 0,
            TargetOsPartitionNumber = 9,
            AllDisks = new[]
            {
                new DiskSnapshot
                {
                    DiskNumber = 0,
                    FriendlyName = "Test Disk",
                    SizeBytes = 500L * 1024 * 1024 * 1024,
                    PartitionStyle = PartitionStyleType.Gpt,
                    Partitions = new[]
                    {
                        new PartitionSnapshot
                        {
                            DiskNumber = 0,
                            PartitionNumber = 1,
                            OffsetBytes = 1024L * 1024,
                            SizeBytes = 100L * 1024 * 1024,
                            Label = "EFI",
                            Role = PartitionRole.EfiSystem,
                            IsSystem = true
                        }
                    }
                }
            }
        };

        SetupPartitionGuide guide = generator.Generate(env, BuildSizingPlan());
        Assert.NotEmpty(guide.CriticalRules);
    }

    private static PartitionSizingPlan BuildSizingPlan() => new()
    {
        IsoSizeBytes = 5L * 1024 * 1024 * 1024,
        PayloadSizeBytes = 4L * 1024 * 1024 * 1024,
        SplitStrategy = WimSplitStrategy.SplitWimToSwm,
        EstimatedSwmCount = 2,
        RequiredTempOsBytes = 1024L * 1024 * 1024,
        InstallerPartitionBytes = 8L * 1024 * 1024 * 1024,
        InstallerPartitionMb = 8_192,
        OsPartitionBeforeBytes = 200L * 1024 * 1024 * 1024,
        OsPartitionAfterBytes = 192L * 1024 * 1024 * 1024,
        RemainingOsFreeAfterShrinkBytes = 64L * 1024 * 1024 * 1024,
        FitsWithinShrinkLimit = true,
        FitsWithinFat32Limit = true
    };
}
