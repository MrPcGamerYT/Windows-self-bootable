using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services.Native;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class RawUefiBootDiagnosticsTests
{
    [Fact]
    public void UnlistedLegacySwbiSlot_IsStillReported_ButNeverConsideredGuidOwnershipProof()
    {
        var store = new ReadOnlyStore();
        store.Set("BootOrder", Slots(2, 1));
        store.Set("BootCurrent", Slots(2));
        store.Set("Boot0001", LoadOption("Hard Drive"));
        store.Set("Boot0002", LoadOption("Windows Boot Manager"));
        store.Set("Boot0010", LoadOption(SwbiBrand.BootChooserEntryName));

        string report = RawUefiBootDiagnostics.Capture(store);

        Assert.Contains("BootOrder: Boot0002, Boot0001", report); // Preserve firmware order, not set order.
        Assert.Contains("BootCurrent: Boot0002", report);
        Assert.Contains("Boot0010: SWBI title, SWBI-shaped EFI load option = NO; in BootOrder = False", report);
        Assert.Contains("relationship to a BCD firmware GUID is NOT established", report);
        Assert.DoesNotContain("No SWBI-titled raw options", report);
        Assert.Contains("Boot004F", store.ReadNames); // Even unlisted legacy slots are inspected.
    }

    [Fact]
    public void BootNextOutsideLegacyRange_IsReadButNeverCleared()
    {
        var store = new ReadOnlyStore();
        store.Set("BootOrder", Slots(1));
        store.Set("BootNext", Slots(0x0050));
        store.Set("Boot0050", LoadOption(SwbiBrand.BootChooserEntryName));

        string report = RawUefiBootDiagnostics.Capture(store);

        Assert.Contains("BootNext: Boot0050 (do not change it)", report);
        Assert.Contains("Boot0050: SWBI title", report);
        Assert.Contains("BootNext = True", report);
    }

    [Fact]
    public void MissingOrUnreadableFirmwareData_NeverClaimsTheGuidCanBeDeleted()
    {
        var missing = new ReadOnlyStore();
        string report = RawUefiBootDiagnostics.Capture(missing);
        Assert.Contains("BootOrder: unavailable", report);
        Assert.Contains("scan INCOMPLETE", report);
        Assert.Contains("does NOT prove", report);

        var corrupt = new ReadOnlyStore { FailOnName = "Boot0010" };
        corrupt.Set("BootOrder", new byte[] { 0x10 });
        report = RawUefiBootDiagnostics.Capture(corrupt);
        Assert.Contains("BootOrder: malformed", report);
        Assert.Contains("Boot0010: unreadable", report);
        Assert.Contains("scan INCOMPLETE", report);
    }

    [Fact]
    public void NoTitlesInBoundedScan_IsNotProofThatBcdFirmwareGuidIsAnOrphan()
    {
        var store = new ReadOnlyStore();
        store.Set("BootOrder", Slots(1));
        store.Set("Boot0001", LoadOption("Windows Boot Manager"));
        string report = RawUefiBootDiagnostics.Capture(store);
        Assert.Contains("No SWBI-titled raw options were found in the slots inspected", report);
        Assert.Contains("does NOT prove", report);
        Assert.Contains("does not authorize cleanup of a BCD firmware GUID", report);
    }

    private static byte[] LoadOption(string description) =>
        new byte[6].Concat(Encoding.Unicode.GetBytes(description + "\0")).ToArray();

    private static byte[] Slots(params ushort[] slots)
    {
        byte[] bytes = new byte[slots.Length * 2];
        for (int i = 0; i < slots.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), slots[i]);
        return bytes;
    }

    private sealed class ReadOnlyStore : IFirmwareVariableStore
    {
        private readonly Dictionary<string, FirmwareVariable> _values = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ReadNames { get; } = new();
        public string? FailOnName { get; set; }
        public void Set(string name, byte[] data) => _values[name] = new FirmwareVariable(data, 7);
        public FirmwareVariable? Read(string name)
        {
            ReadNames.Add(name);
            if (string.Equals(name, FailOnName, StringComparison.OrdinalIgnoreCase))
                throw new Win32Exception(5, "simulated read failure");
            return _values.TryGetValue(name, out FirmwareVariable? value) ? value : null;
        }
        public void Write(string name, FirmwareVariable value) => throw new Xunit.Sdk.XunitException("Read-only diagnostics must never write firmware.");
        public void Delete(string name) => throw new Xunit.Sdk.XunitException("Read-only diagnostics must never delete firmware.");
    }
}
