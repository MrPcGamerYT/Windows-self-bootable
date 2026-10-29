using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Services.Native;

/// <summary>Signals whether a failed firmware registration may still reference the installer partition.</summary>
public sealed class FirmwareBootProvisionException : InvalidOperationException
{
    public FirmwareBootProvisionException(string message, bool cleanupIncomplete, Exception? inner = null)
        : base(message, inner) => CleanupIncomplete = cleanupIncomplete;

    public bool CleanupIncomplete { get; }
}

internal sealed record FirmwareVariable(byte[] Data, uint Attributes);

internal interface IFirmwareVariableStore
{
    // Null means the firmware reported ERROR_ENVVAR_NOT_FOUND, not an arbitrary read failure.
    FirmwareVariable? Read(string name);
    void Write(string name, FirmwareVariable value);
    void Delete(string name);
}

internal sealed class WindowsFirmwareVariableStore : IFirmwareVariableStore
{
    private const int ErrorEnvvarNotFound = 203;

    public FirmwareVariable? Read(string name)
    {
        byte[] buffer = new byte[64 * 1024];
        uint size = Win32StorageInterop.GetFirmwareEnvironmentVariableExW(
            name, Win32StorageInterop.EfiGlobalVariableNamespaceGuid, buffer, (uint)buffer.Length, out uint attributes);
        if (size == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorEnvvarNotFound) return null;
            throw new Win32Exception(error, $"Could not read UEFI variable {name} (Win32 error {error}).");
        }

        return new FirmwareVariable(buffer.AsSpan(0, checked((int)size)).ToArray(), attributes);
    }

    public void Write(string name, FirmwareVariable value)
    {
        if (!Win32StorageInterop.SetFirmwareEnvironmentVariableExW(name,
                Win32StorageInterop.EfiGlobalVariableNamespaceGuid, value.Data, (uint)value.Data.Length, value.Attributes))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Could not write UEFI variable {name} (Win32 error {error}).");
        }
    }

    public void Delete(string name)
    {
        if (!Win32StorageInterop.SetFirmwareEnvironmentVariableExW(name,
                Win32StorageInterop.EfiGlobalVariableNamespaceGuid, null, 0, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorEnvvarNotFound)
            {
                throw new Win32Exception(error, $"Could not delete UEFI variable {name} (Win32 error {error}).");
            }
        }
    }
}

internal static class UefiBootRegistration
{
    private const uint NewVariableAttributes = Win32StorageInterop.EFI_GLOBAL_NV_BS_RT;

    internal static string Register(IFirmwareVariableStore store, byte[] loadOption)
    {
        FirmwareVariable originalOrder = store.Read("BootOrder")
            ?? throw new InvalidOperationException("Firmware BootOrder is unavailable; refusing to alter firmware boot settings.");
        List<ushort> order = DecodeOrder(originalOrder.Data);
        if (order.Count == 0)
        {
            throw new InvalidOperationException("Firmware BootOrder is empty; refusing to replace the normal boot path.");
        }
        if (store.Read("BootNext") is not null)
        {
            throw new InvalidOperationException("A one-time firmware boot is already pending. Complete or cancel it before preparing SWBI.");
        }

        ushort? selected = null;
        for (ushort candidate = 0x0010; candidate <= 0x004F; candidate++)
        {
            if (!order.Contains(candidate) && store.Read($"Boot{candidate:X4}") is null)
            {
                selected = candidate;
                break;
            }
        }
        if (selected is null)
        {
            throw new InvalidOperationException("No free SWBI firmware boot slot is available; no existing entry was overwritten.");
        }

        ushort slot = selected.Value;
        string name = $"Boot{slot:X4}";
        var bootEntry = new FirmwareVariable(loadOption, NewVariableAttributes);
        var newOrder = new FirmwareVariable(EncodeOrder(order.Append(slot)), originalOrder.Attributes);
        var bootNext = new FirmwareVariable(EncodeOrder(new[] { slot }), NewVariableAttributes);
        bool entryAttempted = false;
        bool orderAttempted = false;
        bool nextAttempted = false;

        try
        {
            RequireSame(store.Read("BootOrder"), originalOrder, "Firmware BootOrder changed during preparation.");
            if (store.Read("BootNext") is not null || store.Read(name) is not null)
            {
                throw new InvalidOperationException("Firmware boot settings changed during preparation; nothing was overwritten.");
            }

            entryAttempted = true;
            store.Write(name, bootEntry);
            RequireSame(store.Read(name), bootEntry, $"Firmware did not retain {name}.");
            RequireSame(store.Read("BootOrder"), originalOrder, "Firmware BootOrder changed during preparation.");
            if (store.Read("BootNext") is not null)
            {
                throw new InvalidOperationException("Another one-time firmware boot was scheduled during preparation.");
            }

            orderAttempted = true;
            store.Write("BootOrder", newOrder);
            RequireSame(store.Read("BootOrder"), newOrder, "Firmware did not retain the updated BootOrder.");
            if (store.Read("BootNext") is not null)
            {
                throw new InvalidOperationException("Another one-time firmware boot was scheduled during preparation.");
            }

            nextAttempted = true;
            store.Write("BootNext", bootNext);
            RequireSame(store.Read("BootNext"), bootNext, "Firmware did not retain BootNext.");
            return $"UEFI:{name}";
        }
        catch (Exception error)
        {
            bool cleaned = !entryAttempted || TryRemoveOwned(store, slot, bootEntry, orderAttempted, nextAttempted);
            string cleanup = cleaned
                ? "The temporary firmware entry was removed."
                : "Firmware cleanup could not be verified; keep the installer partition and inspect firmware boot settings before restarting.";
            throw new FirmwareBootProvisionException($"UEFI boot registration failed: {error.Message} {cleanup}", !cleaned, error);
        }
    }

    // Explicit cleanup is deliberately restricted to the requested slot. A reused slot must not be deleted.
    internal static void Remove(IFirmwareVariableStore store, string firmwareId)
    {
        if (!TryParseFirmwareId(firmwareId, out ushort slot))
            throw new InvalidOperationException("Invalid UEFI firmware boot identifier.");

        string name = $"Boot{slot:X4}";
        FirmwareVariable? entry = store.Read(name);
        if (entry is not null && !IsSwbiLoadOption(entry.Data))
        {
            throw new InvalidOperationException($"{name} no longer belongs to SWBI; it was not removed.");
        }

        if (!TryRemoveOwned(store, slot, entry, removeFromOrder: true, removeBootNext: true))
        {
            throw new InvalidOperationException($"Could not verify cleanup of {name}; the installer partition must be kept.");
        }
    }

    internal static bool TryParseFirmwareId(string? firmwareId, out ushort slot)
    {
        const string prefix = "UEFI:Boot";
        slot = 0;
        return firmwareId is not null && firmwareId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            firmwareId.Length == prefix.Length + 4 &&
            ushort.TryParse(firmwareId[prefix.Length..], System.Globalization.NumberStyles.HexNumber,
                null, out slot) && slot >= 0x0010 && slot <= 0x004F;
    }

    private static bool TryRemoveOwned(IFirmwareVariableStore store, ushort slot, FirmwareVariable? expectedEntry,
        bool removeFromOrder, bool removeBootNext)
    {
        try
        {
            string name = $"Boot{slot:X4}";
            FirmwareVariable? currentEntry = store.Read(name);
            if (currentEntry is not null && (expectedEntry is null || !Same(currentEntry, expectedEntry))) return false;

            if (removeBootNext)
            {
                FirmwareVariable? next = store.Read("BootNext");
                if (next is not null)
                {
                    List<ushort> slots = DecodeOrder(next.Data);
                    if (slots.Count != 1) return false;
                    if (slots[0] == slot)
                    {
                        store.Delete("BootNext");
                        FirmwareVariable? after = store.Read("BootNext");
                        if (after is not null && DecodeOrder(after.Data).Contains(slot)) return false;
                    }
                }
            }

            if (removeFromOrder)
            {
                FirmwareVariable? currentOrder = store.Read("BootOrder");
                if (currentOrder is null) return false;
                List<ushort> order = DecodeOrder(currentOrder.Data);
                if (order.RemoveAll(x => x == slot) > 0)
                {
                    if (order.Count == 0) return false; // Never erase the last firmware boot path.
                    var updatedOrder = new FirmwareVariable(EncodeOrder(order), currentOrder.Attributes);
                    store.Write("BootOrder", updatedOrder);
                    if (!Same(store.Read("BootOrder"), updatedOrder)) return false;
                }
            }

            currentEntry = store.Read(name);
            if (currentEntry is not null)
            {
                if (expectedEntry is null || !Same(currentEntry, expectedEntry)) return false;
                store.Delete(name);
                if (store.Read(name) is not null) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsSwbiLoadOption(byte[] data)
    {
        if (data.Length < 8) return false; // EFI_LOAD_OPTION header is 6 bytes.
        int end = 6;
        while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0)) end += 2;
        if (end + 1 >= data.Length) return false;
        return SwbiBrand.IsRecognizedBootDescription(Encoding.Unicode.GetString(data, 6, end - 6));
    }

    private static List<ushort> DecodeOrder(byte[] data)
    {
        if ((data.Length & 1) != 0) throw new InvalidOperationException("Malformed UEFI boot variable.");
        var result = new List<ushort>(data.Length / 2);
        for (int i = 0; i < data.Length; i += 2)
            result.Add(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i, 2)));
        return result;
    }

    private static byte[] EncodeOrder(IEnumerable<ushort> values)
    {
        ushort[] items = values.ToArray();
        byte[] data = new byte[items.Length * 2];
        for (int i = 0; i < items.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2, 2), items[i]);
        return data;
    }

    private static bool Same(FirmwareVariable? left, FirmwareVariable right) =>
        left is not null && left.Attributes == right.Attributes && left.Data.AsSpan().SequenceEqual(right.Data);

    private static void RequireSame(FirmwareVariable? actual, FirmwareVariable expected, string error)
    {
        if (!Same(actual, expected)) throw new InvalidOperationException(error);
    }
}
