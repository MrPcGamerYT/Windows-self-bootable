using System.Buffers.Binary;
using System.Text;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services;
using UsbFreeInstaller.Core.Services.Native;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class BootSafetyTests
{
    [Fact]
    public async Task Uefi_UsesOnlyFirmwareAndNeverBacksUpOrWritesSystemBcd()
    {
        var backend = new FakeBootBackend();
        var service = new WindowsBcdBootService(new SafeRedactingLogger(), backend);
        BcdProvisionResult result = await service.ConfigureOneTimeWinPeBootAsync("W:", "unused", FirmwareMode.Uefi);
        Assert.Equal(new[] { "uefi" }, backend.Calls);
        Assert.Equal("UEFI:Boot0010", result.FirmwareBootGuid);
        Assert.Null(result.BcdBackupFilePath);
        Assert.Null(result.OsLoaderGuid);
        Assert.Null(result.SdiDeviceGuid);
    }

    [Fact]
    public async Task Legacy_UsesOneBcdSequenceAndRequiresBackup()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"swbi-test-{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeBootBackend();
            var service = new WindowsBcdBootService(new SafeRedactingLogger(), backend);
            BcdProvisionResult result = await service.ConfigureOneTimeWinPeBootAsync("W", dir, FirmwareMode.LegacyBios);
            Assert.Equal(new[] { "backup", "inspect", "legacy" }, backend.Calls);
            Assert.NotNull(result.OsLoaderGuid);
            Assert.Null(result.FirmwareBootGuid);

            var failureBackend = new FakeBootBackend { BackupSucceeds = false };
            service = new WindowsBcdBootService(new SafeRedactingLogger(), failureBackend);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfigureOneTimeWinPeBootAsync("W", dir, FirmwareMode.LegacyBios));
            Assert.Equal(new[] { "backup" }, failureBackend.Calls);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task UnknownFirmwareAndExistingLegacyEntries_FailWithoutProvisioning()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"swbi-test-{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeBootBackend { ExistingLoader = "{11111111-1111-1111-1111-111111111111}" };
            var service = new WindowsBcdBootService(new SafeRedactingLogger(), backend);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfigureOneTimeWinPeBootAsync("W", dir, FirmwareMode.Unknown));
            Assert.Empty(backend.Calls);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfigureOneTimeWinPeBootAsync("W", dir, FirmwareMode.LegacyBios));
            Assert.Equal(new[] { "backup", "inspect" }, backend.Calls);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void DirectSystemBcdProvision_RejectsUefiBeforeAccessingRegistry()
    {
        Assert.Throws<InvalidOperationException>(() =>
            NativeUefiAndBcdRegistryEngine.ProvisionSystemBcdInRegistry("W", FirmwareMode.Uefi, new SafeRedactingLogger()));
    }

    [Fact]
    public void FirmwareRegistration_PreservesPrimaryOrderAndManualEntryAfterBootNextIsConsumed()
    {
        var store = CreateStore();
        byte[] loadOption = SwbiLoadOption();
        string id = UefiBootRegistration.Register(store, loadOption);
        Assert.Equal("UEFI:Boot0010", id);
        Assert.Equal(new ushort[] { 1, 2, 0x10 }, Slots(store.Read("BootOrder")!.Data));
        Assert.Equal(new ushort[] { 0x10 }, Slots(store.Read("BootNext")!.Data));
        Assert.Equal(loadOption, store.Read("Boot0010")!.Data);
        Assert.Equal((uint)7, store.Read("BootOrder")!.Attributes);

        store.Delete("BootNext"); // Firmware consumes the one-time request.
        Assert.Equal(loadOption, store.Read("Boot0010")!.Data);
        Assert.Equal(new ushort[] { 1, 2, 0x10 }, Slots(store.Read("BootOrder")!.Data));
        UefiBootRegistration.Remove(store, id);
        Assert.Null(store.Read("Boot0010"));
        Assert.Equal(new ushort[] { 1, 2 }, Slots(store.Read("BootOrder")!.Data));
        Assert.NotNull(store.Read("Boot0001"));
    }

    [Fact]
    public void PendingBootNext_IsNotOverwritten()
    {
        var store = CreateStore();
        store.Set("BootNext", Order(2));
        Assert.Throws<InvalidOperationException>(() => UefiBootRegistration.Register(store, SwbiLoadOption()));
        Assert.Equal(new ushort[] { 2 }, Slots(store.Read("BootNext")!.Data));
        Assert.Null(store.Read("Boot0010"));
    }

    [Fact]
    public void FullSlotRange_DoesNotOverwriteAnyExistingEntry()
    {
        var store = CreateStore();
        for (int i = 0x10; i <= 0x4F; i++) store.Set($"Boot{i:X4}", new byte[] { 1, 2, 3 });
        Assert.Throws<InvalidOperationException>(() => UefiBootRegistration.Register(store, SwbiLoadOption()));
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Read("Boot0019")!.Data);
        Assert.Equal(new ushort[] { 1, 2 }, Slots(store.Read("BootOrder")!.Data));
        Assert.Null(store.Read("BootNext"));
    }

    [Theory]
    [InlineData("Boot0010", false)]
    [InlineData("Boot0010", true)]
    [InlineData("BootOrder", false)]
    [InlineData("BootOrder", true)]
    [InlineData("BootNext", false)]
    [InlineData("BootNext", true)]
    public void FailedWrites_RollBackOwnedFirmwareChanges(string failure, bool afterWrite)
    {
        var store = CreateStore();
        store.FailWriteOnce = failure;
        store.FailAfterWrite = afterWrite;
        var error = Assert.Throws<FirmwareBootProvisionException>(() => UefiBootRegistration.Register(store, SwbiLoadOption()));
        Assert.False(error.CleanupIncomplete);
        Assert.Equal(new ushort[] { 1, 2 }, Slots(store.Read("BootOrder")!.Data));
        Assert.Null(store.Read("BootNext"));
        Assert.Null(store.Read("Boot0010"));
        Assert.NotNull(store.Read("Boot0001"));
    }

    [Fact]
    public void UnverifiedRollback_KeepsEntryAndReportsIncomplete()
    {
        var store = CreateStore();
        store.FailWriteOnce = "BootNext";
        store.FailAfterWrite = true;
        store.FailDelete = "BootNext";
        var error = Assert.Throws<FirmwareBootProvisionException>(() => UefiBootRegistration.Register(store, SwbiLoadOption()));
        Assert.True(error.CleanupIncomplete);
        Assert.NotNull(store.Read("Boot0010"));
        Assert.Equal(new ushort[] { 0x10 }, Slots(store.Read("BootNext")!.Data));
    }

    [Fact]
    public void ConcurrentUnrelatedBootNext_IsNotDeletedDuringRollback()
    {
        var store = CreateStore();
        store.OnWrite = (name, s) => { if (name == "Boot0010") s.Set("BootNext", Order(2)); };
        var error = Assert.Throws<FirmwareBootProvisionException>(() => UefiBootRegistration.Register(store, SwbiLoadOption()));
        Assert.False(error.CleanupIncomplete);
        Assert.Equal(new ushort[] { 2 }, Slots(store.Read("BootNext")!.Data));
        Assert.Null(store.Read("Boot0010"));
    }

    [Fact]
    public void Cleanup_RefusesToDeleteAReusedSlot()
    {
        var store = CreateStore();
        store.Set("Boot0010", LoadOption("Someone else's operating system"));
        store.Set("BootOrder", Order(1, 2, 0x10));
        Assert.Throws<InvalidOperationException>(() => UefiBootRegistration.Remove(store, "UEFI:Boot0010"));
        Assert.NotNull(store.Read("Boot0010"));
        Assert.Equal(new ushort[] { 1, 2, 0x10 }, Slots(store.Read("BootOrder")!.Data));
    }

    [Fact]
    public void Cleanup_RejectsFirmwareThatTruncatesOtherBootOrderEntries()
    {
        var store = CreateStore();
        store.Set("Boot0010", SwbiLoadOption());
        store.Set("BootOrder", Order(1, 2, 0x10));
        store.OnWrite = (name, s) => { if (name == "BootOrder") s.Set("BootOrder", Order(1)); };
        Assert.Throws<InvalidOperationException>(() => UefiBootRegistration.Remove(store, "UEFI:Boot0010"));
        Assert.NotNull(store.Read("Boot0010"));
    }

    [Theory]
    [InlineData("UEFI:Boot0001")]
    [InlineData("UEFI:Boot001")]
    [InlineData("UEFI:Boot0050")]
    [InlineData("UEFI:BootZZZZ")]
    public void Cleanup_RejectsIdsOutsideTheOwnedRange(string id)
    {
        var store = CreateStore();
        Assert.Throws<InvalidOperationException>(() => UefiBootRegistration.Remove(store, id));
        Assert.NotNull(store.Read("Boot0001"));
        Assert.Equal(new ushort[] { 1, 2 }, Slots(store.Read("BootOrder")!.Data));
    }

    [Fact]
    public void Cleanup_NeverRemovesTheLastBootPath()
    {
        var store = CreateStore();
        store.Set("Boot0010", SwbiLoadOption());
        store.Set("BootOrder", Order(0x10));
        Assert.Throws<InvalidOperationException>(() => UefiBootRegistration.Remove(store, "UEFI:Boot0010"));
        Assert.NotNull(store.Read("Boot0010"));
    }

    private static FakeStore CreateStore()
    {
        var store = new FakeStore();
        store.Set("BootOrder", Order(1, 2));
        store.Set("Boot0001", LoadOption("Windows Boot Manager"));
        store.Set("Boot0002", LoadOption("Other OS"));
        return store;
    }

    private static byte[] SwbiLoadOption() => LoadOption(SwbiBrand.BootChooserEntryName);
    private static byte[] LoadOption(string description) => new byte[6].Concat(Encoding.Unicode.GetBytes(description + "\0")).ToArray();
    private static byte[] Order(params ushort[] slots)
    {
        byte[] value = new byte[slots.Length * 2];
        for (int i = 0; i < slots.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(i * 2, 2), slots[i]);
        return value;
    }
    private static ushort[] Slots(byte[] bytes) => Enumerable.Range(0, bytes.Length / 2)
        .Select(i => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2))).ToArray();

    private sealed class FakeStore : IFirmwareVariableStore
    {
        private readonly Dictionary<string, FirmwareVariable> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? FailWriteOnce { get; set; }
        public bool FailAfterWrite { get; set; }
        public string? FailDelete { get; set; }
        public Action<string, FakeStore>? OnWrite { get; set; }
        public FirmwareVariable? Read(string name) => _values.TryGetValue(name, out FirmwareVariable? value)
            ? new FirmwareVariable(value.Data.ToArray(), value.Attributes) : null;
        public void Set(string name, byte[] data) => _values[name] = new FirmwareVariable(data.ToArray(), 7);
        public void Write(string name, FirmwareVariable value)
        {
            bool fail = name == FailWriteOnce;
            if (fail) FailWriteOnce = null;
            if (fail && !FailAfterWrite) throw new IOException("injected firmware write failure");
            _values[name] = new FirmwareVariable(value.Data.ToArray(), value.Attributes);
            OnWrite?.Invoke(name, this);
            if (fail) throw new IOException("injected firmware write failure after change");
        }
        public void Delete(string name)
        {
            if (name == FailDelete) throw new IOException("injected firmware delete failure");
            _values.Remove(name);
        }
    }

    private sealed class FakeBootBackend : IBootProvisioningBackend
    {
        public List<string> Calls { get; } = new();
        public bool BackupSucceeds { get; init; } = true;
        public string? ExistingLoader { get; init; }
        public string RegisterUefi(string drive, ISafeLogger logger) { Calls.Add("uefi"); return "UEFI:Boot0010"; }
        public bool BackupSystemBcd(string path, ISafeLogger logger) { Calls.Add("backup"); return BackupSucceeds; }
        public ActiveBcdState InspectSystemBcd()
        {
            Calls.Add("inspect");
            return new ActiveBcdState(false, "", "", ExistingLoader, null);
        }
        public (string SdiGuid, string LoaderGuid) RegisterLegacyBcd(string drive, ISafeLogger logger)
        {
            Calls.Add("legacy");
            return ("{11111111-1111-1111-1111-111111111111}", "{22222222-2222-2222-2222-222222222222}");
        }
    }
}
