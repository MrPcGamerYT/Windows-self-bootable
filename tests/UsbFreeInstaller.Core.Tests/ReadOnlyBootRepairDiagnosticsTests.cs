using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class ReadOnlyBootRepairDiagnosticsTests
{
    [Fact]
    public void DiagnosisPlan_UsesOnlyReadOnlyEnum_AndNeverInterpolatesUntrustedIdentifiers()
    {
        CleanupInspectionResult inspection = BlockedFirmwareGuid() with
        {
            OrphanedBcdLoaderGuid = "{11111111-2222-4444-8888-123456789abc}",
            OrphanedBcdSdiGuid = "{11111111-2222-4444-8888-123456789abc}", // Deduplicated.
        };
        var queries = WindowsReadOnlyBootRepairDiagnostics.BuildQueries(inspection);

        Assert.Equal(4, queries.Count);
        Assert.Equal("{aaaaaaaa-1111-4444-8888-000000000010}", queries[0].Identifier);
        Assert.Equal("{11111111-2222-4444-8888-123456789abc}", queries[1].Identifier);
        Assert.Equal("firmware", queries[2].Identifier);
        Assert.Equal("{bootmgr}", queries[3].Identifier);
        Assert.All(queries, query =>
        {
            Assert.Equal(new[] { "/enum", query.Identifier, "/v" }, query.Arguments);
            Assert.DoesNotContain("/delete", string.Join(' ', query.Arguments), StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("{aaaaaaaa-1111-4444-8888-000000000010} /delete {bootmgr}")]
    [InlineData("UEFI:Boot0010")]
    [InlineData("{bootmgr}")]
    [InlineData("{00000000-0000-0000-0000-000000000000}")]
    public void DiagnosisPlan_RejectsUnsupportedOrInjectedGuidTargets(string untrusted)
    {
        // A syntactically valid reserved GUID would still be a read-only /enum query.
        CleanupInspectionResult inspection = BlockedFirmwareGuid() with
        {
            OrphanedBcdFirmwareGuid = untrusted,
            OrphanedBcdLoaderGuid = null,
            OrphanedBcdSdiGuid = null
        };
        var queries = WindowsReadOnlyBootRepairDiagnostics.BuildQueries(inspection);
        Assert.Equal(new[] { "firmware", "{bootmgr}" }, queries.Select(q => q.Identifier));
    }

    private static CleanupInspectionResult BlockedFirmwareGuid() => new()
    {
        State = CleanupSafetyState.BlockedAmbiguousPartition,
        CurrentOsDiskNumber = 0,
        CurrentOsPartitionNumber = 3,
        CurrentBootUsesRamdisk = false,
        CurrentBootDependsOnInstaller = false,
        OrphanedBcdFirmwareGuid = "{aaaaaaaa-1111-4444-8888-000000000010}"
    };
}
