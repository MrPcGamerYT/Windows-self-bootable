using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;

namespace UsbFreeInstaller.Core.Services.Native;

/// <summary>
/// Bounded, strictly read-only inspection of raw EFI_GLOBAL BootXXXX variables. BCD
/// firmware application GUIDs do not identify BootXXXX slots; even a matching title
/// or a SWBI-shaped load option is evidence for manual review, NOT deletion authority.
/// </summary>
internal static class RawUefiBootDiagnostics
{
    private const int MaxOrderEntries = 256;
    private const int MaxVariableReadErrors = 3;

    internal static string Capture(IFirmwareVariableStore store, CancellationToken cancellationToken = default)
    {
        var report = new StringBuilder();
        report.AppendLine("RAW UEFI BOOT VARIABLES (read-only; no BCD GUID-to-BootXXXX mapping inferred)");
        report.AppendLine("Inspects BootOrder, BootNext, BootCurrent, and the legacy SWBI slot range Boot0010–Boot004F.");

        bool complete = true;
        var slots = new HashSet<ushort>();
        var order = new HashSet<ushort>();
        var orderedSlots = new List<ushort>();
        ushort? next = null;
        ushort? current = null;
        bool currentKnown = false;

        try
        {
            FirmwareVariable? value = store.Read("BootOrder");
            if (value is null)
            {
                complete = false;
                report.AppendLine("BootOrder: unavailable or absent; cannot establish the active firmware order.");
            }
            else if (value.Data.Length == 0 || value.Data.Length % 2 != 0 || value.Data.Length / 2 > MaxOrderEntries)
            {
                complete = false;
                report.AppendLine("BootOrder: malformed, empty, or too long to inspect safely; no absence can be inferred.");
            }
            else
            {
                for (int i = 0; i < value.Data.Length; i += 2)
                {
                    ushort slot = BinaryPrimitives.ReadUInt16LittleEndian(value.Data.AsSpan(i, 2));
                    order.Add(slot);
                    orderedSlots.Add(slot);
                    slots.Add(slot);
                }
                report.AppendLine($"BootOrder: {string.Join(", ", orderedSlots.Select(FormatSlot))}");
            }
        }
        catch (Win32Exception ex)
        {
            complete = false;
            report.AppendLine($"BootOrder: unreadable (Win32 {ex.NativeErrorCode}); no absence can be inferred.");
        }

        try
        {
            FirmwareVariable? value = store.Read("BootNext");
            if (value is null) report.AppendLine("BootNext: absent (no one-time firmware selection was reported).");
            else if (value.Data.Length != 2)
            {
                complete = false;
                report.AppendLine("BootNext: malformed or unreadable; no absence can be inferred.");
            }
            else
            {
                next = BinaryPrimitives.ReadUInt16LittleEndian(value.Data);
                slots.Add(next.Value);
                report.AppendLine($"BootNext: {FormatSlot(next.Value)} (do not change it).");
            }
        }
        catch (Win32Exception ex)
        {
            complete = false;
            report.AppendLine($"BootNext: unreadable (Win32 {ex.NativeErrorCode}); do not change it.");
        }

        try
        {
            FirmwareVariable? value = store.Read("BootCurrent");
            if (value?.Data.Length == 2)
            {
                current = BinaryPrimitives.ReadUInt16LittleEndian(value.Data);
                currentKnown = true;
                slots.Add(current.Value);
                report.AppendLine($"BootCurrent: {FormatSlot(current.Value)} (the current firmware boot path; do not remove it).");
            }
            else
            {
                report.AppendLine("BootCurrent: not reported; the active firmware slot is unverified.");
            }
        }
        catch (Win32Exception ex)
        {
            report.AppendLine($"BootCurrent: unreadable (Win32 {ex.NativeErrorCode}); the active firmware slot is unverified.");
        }

        for (ushort slot = 0x0010; slot <= 0x004F; slot++) slots.Add(slot);
        int titleMatches = 0;
        int readErrors = 0;
        foreach (ushort slot in slots.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            FirmwareVariable? option;
            try
            {
                option = store.Read($"Boot{slot:X4}");
            }
            catch (Win32Exception ex)
            {
                complete = false;
                readErrors++;
                report.AppendLine($"{FormatSlot(slot)}: unreadable (Win32 {ex.NativeErrorCode}); no absence can be inferred.");
                if (readErrors >= MaxVariableReadErrors)
                {
                    report.AppendLine("Stopped after repeated UEFI read errors; inspection is incomplete.");
                    break;
                }
                continue;
            }

            if (option is null)
            {
                if (order.Contains(slot) || next == slot || current == slot)
                {
                    complete = false;
                    report.AppendLine($"{FormatSlot(slot)} is referenced but its variable is missing; inspection is incomplete.");
                }
                continue;
            }
            if (!UefiBootRegistration.HasSwbiDescription(option.Data)) continue;

            titleMatches++;
            bool shaped = UefiBootRegistration.IsSwbiLoadOption(option.Data);
            report.AppendLine($"{FormatSlot(slot)}: SWBI title, SWBI-shaped EFI load option = {(shaped ? "yes" : "NO")}; " +
                $"in BootOrder = {order.Contains(slot)}; BootNext = {next == slot}; " +
                $"BootCurrent = {(currentKnown ? (current == slot).ToString() : "unknown")}. " +
                "Its relationship to a BCD firmware GUID is NOT established.");
        }

        if (titleMatches == 0)
            report.AppendLine("No SWBI-titled raw options were found in the slots inspected. This does NOT prove that an out-of-range/unreadable BootXXXX variable or the BCD firmware GUID is safe to delete.");
        report.AppendLine(complete
            ? "Bounded read complete. No boot configuration was changed; this report does not authorize cleanup of a BCD firmware GUID."
            : "Read-only scan INCOMPLETE. Do not infer that the BCD firmware GUID is orphaned or safe to delete.");
        return report.ToString();
    }

    private static string FormatSlot(ushort slot) => $"Boot{slot:X4}";
}
