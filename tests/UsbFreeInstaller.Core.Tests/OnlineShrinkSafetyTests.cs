using System.Buffers.Binary;
using UsbFreeInstaller.Core.Services.Native;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class OnlineShrinkSafetyTests
{
    [Fact]
    public void Success_CommitsAndVerifiesFilesystemBeforeResizingPartition()
    {
        var operations = new FakeShrinkOperations();
        OnlineShrinkTransaction.Execute(operations);
        Assert.Equal(new[] { "prepare", "evacuate", "commit", "verify-target", "resize", "verify-result" }, operations.Calls);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("evacuate")]
    [InlineData("commit")]
    public void BeforeCommitFailure_AbortsAndNeverResizes(string failure)
    {
        var operations = new FakeShrinkOperations { FailAt = failure };
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => OnlineShrinkTransaction.Execute(operations));
        Assert.Contains("before the partition was resized", error.Message);
        Assert.Contains("abort", operations.Calls);
        Assert.DoesNotContain("resize", operations.Calls);
        Assert.DoesNotContain("restore", operations.Calls);
    }

    [Fact]
    public void AbortFailure_IsReportedAndStillNeverResizes()
    {
        var operations = new FakeShrinkOperations { FailAt = "commit", AbortFails = true };
        var error = Assert.Throws<InvalidOperationException>(() => OnlineShrinkTransaction.Execute(operations));
        Assert.Contains("abort also failed", error.Message);
        Assert.DoesNotContain("resize", operations.Calls);
    }

    [Fact]
    public void BadPostCommitGeometry_DoesNotResizeOrCreatePartition()
    {
        var operations = new FakeShrinkOperations { FailAt = "verify-target" };
        var error = Assert.Throws<InvalidOperationException>(() => OnlineShrinkTransaction.Execute(operations));
        Assert.Contains("partition resize was not attempted", error.Message);
        Assert.DoesNotContain("resize", operations.Calls);
        Assert.DoesNotContain("abort", operations.Calls);
    }

    [Theory]
    [InlineData("resize")]
    [InlineData("verify-result")]
    public void AfterResizeFailure_AttemptsPartitionLengthRestoreAndReportsSmallerFilesystem(string failure)
    {
        var operations = new FakeShrinkOperations { FailAt = failure, RestoreResult = true };
        var error = Assert.Throws<InvalidOperationException>(() => OnlineShrinkTransaction.Execute(operations));
        Assert.Equal("restore", operations.Calls[^1]);
        Assert.Contains("original partition length was verified or restored", error.Message);
        Assert.Contains("filesystem may remain smaller", error.Message);
    }

    [Fact]
    public void RestoreFailure_IsNeverReportedAsSafeRollback()
    {
        var operations = new FakeShrinkOperations { FailAt = "resize", RestoreResult = false };
        var error = Assert.Throws<InvalidOperationException>(() => OnlineShrinkTransaction.Execute(operations));
        Assert.Contains("could not be verified or restored", error.Message);
        Assert.Contains("recovery is not automatic", error.Message);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void BootSector_ParsesAndAcceptsMatchingGeometry(int sectorBytes)
    {
        byte[] sector = MakeBootSector(sectorBytes, 1000);
        NtfsBootSectorInfo info = NtfsBootSector.Parse(sector);
        var geometry = new NtfsVolumeGeometry(1000, 125, 10, sectorBytes, sectorBytes * 8, 0, 0);
        NtfsBootSector.VerifyFits(info, geometry, (long)sectorBytes * 1000);
        Assert.Equal((long)sectorBytes * 1000, info.LengthBytes);
    }

    [Fact]
    public void BootSector_RejectsOversizedAndMismatchedVolumes()
    {
        NtfsBootSectorInfo info = NtfsBootSector.Parse(MakeBootSector(512, 1001));
        var geometry = new NtfsVolumeGeometry(1001, 125, 10, 512, 4096, 0, 0);
        Assert.Throws<InvalidOperationException>(() => NtfsBootSector.VerifyFits(info, geometry, 1000L * 512));
        Assert.Throws<InvalidOperationException>(() => NtfsBootSector.VerifyFits(info, geometry with { TotalSectors = 1000 }, 2000L * 512));
        Assert.Throws<InvalidOperationException>(() => NtfsBootSector.VerifyFits(info, geometry with { BytesPerSector = 4096 }, 2000L * 512));
    }

    [Fact]
    public void BootSector_RejectsInvalidOrOverflowingData()
    {
        byte[] invalid = MakeBootSector(512, 100);
        invalid[3] = 0;
        Assert.Throws<InvalidOperationException>(() => NtfsBootSector.Parse(invalid));
        Assert.Throws<InvalidOperationException>(() => NtfsBootSector.Parse(new byte[32]));
        Assert.Throws<InvalidOperationException>(() => NtfsBootSector.Parse(MakeBootSector(512, long.MaxValue)));
        Assert.Throws<InvalidOperationException>(() => NtfsBootSector.Parse(MakeBootSector(512, 0)));
        byte[] missingSignature = MakeBootSector(1024, 100);
        missingSignature[510] = 0;
        Assert.Throws<InvalidOperationException>(() => NtfsBootSector.Parse(missingSignature));
    }

    private static byte[] MakeBootSector(int bytesPerSector, long totalSectors)
    {
        byte[] result = new byte[bytesPerSector];
        "NTFS    "u8.CopyTo(result.AsSpan(3));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(11, 2), (ushort)bytesPerSector);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(40, 8), totalSectors);
        result[510] = 0x55;
        result[511] = 0xAA;
        return result;
    }

    private sealed class FakeShrinkOperations : IOnlineShrinkOperations
    {
        public List<string> Calls { get; } = new();
        public string? FailAt { get; init; }
        public bool AbortFails { get; init; }
        public bool RestoreResult { get; init; }

        private void Call(string name)
        {
            Calls.Add(name);
            if (FailAt == name) throw new IOException($"Injected {name} failure");
        }

        public void Prepare() => Call("prepare");
        public void Evacuate() => Call("evacuate");
        public void Commit() => Call("commit");
        public void VerifyFileSystemFitsTarget() => Call("verify-target");
        public void ResizePartition() => Call("resize");
        public void VerifyResult() => Call("verify-result");
        public void Abort() { Call("abort"); if (AbortFails) throw new IOException("Injected abort failure"); }
        public bool TryRestorePartitionLength() { Call("restore"); return RestoreResult; }
    }
}

public sealed class PartitionLayoutSafetyTests
{
    private static readonly Guid DiskId = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid OsId = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid InstallerId = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    private static RawPartitionLayoutEntry Part(long start, long length, Guid id, int number = 1) =>
        new(Win32StorageInterop.PARTITION_STYLE_GPT, start, length, number, Guid.Empty, id, 0,
            string.Empty, 0, false, false, 0, Guid.Empty);
    private static RawDiskLayoutSnapshot Layout(params RawPartitionLayoutEntry[] entries) =>
        new(Win32StorageInterop.PARTITION_STYLE_GPT, DiskId, 0, 100_000, 128, 0, 0, entries);

    [Fact]
    public void Exact_nonoverlapping_volume_is_allowed()
    {
        var os = Part(1_000, 4_000, OsId);
        var setup = Part(5_000, 2_000, InstallerId, 2);
        Assert.Equal(os, PartitionLayoutSafety.VerifyBeforeFormatting(Layout(os), os, Layout(os, setup),
            0, 5_000, 2_000, InstallerId, 0, 5_000, 2_000));
    }

    [Theory]
    [InlineData(1, 5000, 2000)] // another disk
    [InlineData(0, 5001, 2000)] // another offset
    [InlineData(0, 5000, 1999)] // partial/multi-volume extent
    public void Mismatched_volume_is_not_formatted(int disk, long start, long length)
    {
        var os = Part(1_000, 4_000, OsId);
        var setup = Part(5_000, 2_000, InstallerId, 2);
        Assert.Throws<InvalidOperationException>(() => PartitionLayoutSafety.VerifyBeforeFormatting(
            Layout(os), os, Layout(os, setup), 0, 5_000, 2_000, InstallerId, disk, start, length));
    }

    [Fact]
    public void Overlapping_partition_is_not_formatted()
    {
        var os = Part(1_000, 4_000, OsId);
        var setup = Part(5_000, 2_000, InstallerId, 2);
        var other = Part(6_000, 2_000, Guid.NewGuid(), 3);
        Assert.Throws<InvalidOperationException>(() => PartitionLayoutSafety.VerifyBeforeFormatting(
            Layout(os), os, Layout(os, setup, other), 0, 5_000, 2_000, InstallerId, 0, 5_000, 2_000));
    }

    [Fact]
    public void Different_installer_guid_or_changed_os_length_is_not_formatted()
    {
        var os = Part(1_000, 4_000, OsId);
        var setup = Part(5_000, 2_000, Guid.NewGuid(), 2);
        Assert.Throws<InvalidOperationException>(() => PartitionLayoutSafety.VerifyBeforeFormatting(
            Layout(os), os, Layout(os, setup), 0, 5_000, 2_000, InstallerId, 0, 5_000, 2_000));
        setup = setup with { GptPartitionId = InstallerId };
        Assert.Throws<InvalidOperationException>(() => PartitionLayoutSafety.VerifyBeforeFormatting(
            Layout(os), os, Layout(os with { PartitionLengthBytes = 3_000 }, setup), 0, 5_000, 2_000,
            InstallerId, 0, 5_000, 2_000));
    }
    [Fact]
    public void FormattingSectorSize_MustBeKnownAndConsistent()
    {
        Assert.Equal(4096, NativeFat32Formatter.SelectVerifiedSectorSize(4096, 4096));
        Assert.Equal(512, NativeFat32Formatter.SelectVerifiedSectorSize(512, null));
        Assert.Throws<InvalidOperationException>(() => NativeFat32Formatter.SelectVerifiedSectorSize(null, null));
        Assert.Throws<InvalidOperationException>(() => NativeFat32Formatter.SelectVerifiedSectorSize(512, 4096));
        Assert.Throws<InvalidOperationException>(() => NativeFat32Formatter.SelectVerifiedSectorSize(768, null));
    }

    [Fact]
    public void MbrPartitionIdentity_RejectsReplacementAtTheSameOffset()
    {
        var mbr = new RawPartitionLayoutEntry(Win32StorageInterop.PARTITION_STYLE_MBR,
            1_000, 4_000, 1, Guid.Empty, Guid.Empty, 0, string.Empty, 0x07, true, true, 2, Guid.Empty);
        Assert.True(PartitionLayoutSafety.IsSamePartition(mbr, mbr with { PartitionLengthBytes = 3_000 }));
        Assert.False(PartitionLayoutSafety.IsSamePartition(mbr, mbr with { MbrPartitionType = 0x0C }));
        Assert.False(PartitionLayoutSafety.IsSamePartition(mbr, mbr with { MbrHiddenSectors = 3 }));
        Assert.False(PartitionLayoutSafety.IsSamePartition(mbr, mbr with { MbrBootIndicator = false }));
    }
}
