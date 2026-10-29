using System.Text;
using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Services;

public sealed class SetupGuideGenerator
{
    public SetupPartitionGuide Generate(
        SystemEnvironmentSnapshot env,
        PartitionSizingPlan sizingPlan)
    {
        DiskSnapshot? disk = env.TargetDisk;
        if (disk is null)
        {
            return BuildFallbackGuide(env, sizingPlan, "The target disk snapshot was unavailable when the guide was generated.");
        }

        if (env.TargetOsPartitionNumber <= 0 || disk.Partitions.All(p => p.PartitionNumber != env.TargetOsPartitionNumber))
        {
            return BuildFallbackGuide(env, sizingPlan, "The old Windows partition could not be re-identified on the target disk.");
        }

        bool isMbrDisk = disk.PartitionStyle == PartitionStyleType.Mbr;
        bool hasSeparateMbrSystemPart = isMbrDisk && disk.Partitions.Any(p =>
            p.PartitionNumber != env.TargetOsPartitionNumber &&
            (p.IsSystem || p.IsActiveLegacy || p.Role == PartitionRole.SystemReservedLegacy));
        bool mustFormatInPlaceMbr = isMbrDisk && (!hasSeparateMbrSystemPart || disk.EffectiveMbrPrimarySlotsUsed >= 3);

        var rows = new List<SetupPartitionRow>();
        IReadOnlyList<PartitionSnapshot> ordered = disk.Partitions.OrderBy(p => p.OffsetBytes).ToArray();

        int nextPartitionNumber = 1;
        foreach (PartitionSnapshot part in ordered)
        {
            if (part.PartitionNumber == env.TargetOsPartitionNumber)
            {
                // 1. Shrunk old Windows partition — THE ONE THE USER FORMATS OR DELETES IN WINDOWS SETUP
                string oldLabel = string.IsNullOrWhiteSpace(part.Label) ? "Windows" : part.Label;
                rows.Add(new SetupPartitionRow
                {
                    DiskNumber = disk.DiskNumber,
                    PartitionNumber = nextPartitionNumber,
                    SetupName = $"Disk {disk.DiskNumber} Partition {nextPartitionNumber}: {oldLabel}",
                    TypeLabel = isMbrDisk && part.IsActiveLegacy ? "System (Active MBR)" : "Primary",
                    SizeBytes = sizingPlan.OsPartitionAfterBytes,
                    Action = SetupPartitionAction.DeleteOrFormatTarget,
                    ActionBadgeText = mustFormatInPlaceMbr ? "FORMAT THIS ONE (DO NOT DELETE)" : "DELETE OR FORMAT THIS ONE",
                    Explanation = mustFormatInPlaceMbr
                        ? "Your shrunk old Windows MBR partition. Click FORMAT (do NOT click Delete) on this partition, then select it and click Next — this preserves the Active MBR boot flag and stays within MBR's 4-primary-slot limit!"
                        : "Your old Windows partition (shrunk). In Windows Setup, select ONLY this partition, click Delete, then select the resulting Unallocated Space and click Next."
                });
                nextPartitionNumber++;

                // 2. Newly created adjacent SWBI — Self Windows Bootable Installer partition — MUST BE PRESERVED
                rows.Add(new SetupPartitionRow
                {
                    DiskNumber = disk.DiskNumber,
                    PartitionNumber = nextPartitionNumber,
                    SetupName = $"Disk {disk.DiskNumber} Partition {nextPartitionNumber}: {SwbiBrand.PartitionVolumeLabel}",
                    TypeLabel = "Primary",
                    SizeBytes = sizingPlan.InstallerPartitionBytes,
                    Action = SetupPartitionAction.PreserveInstallerSource,
                    ActionBadgeText = $"DO NOT TOUCH — {SwbiBrand.ShortName} INSTALLER",
                    Explanation =
                        $"{SwbiBrand.BrandedTitle} media ({sizingPlan.InstallerSizeDisplay}, FAT32 {disk.PartitionStyle}, label '{SwbiBrand.PartitionVolumeLabel}', drivers in '{SwbiBrand.DriverChooserFolderName}'). Deleting this partition destroys your installation source!"
                });
                nextPartitionNumber++;
            }
            else
            {
                (string typeLabel, SetupPartitionAction action, string badge, string explanation) = part.Role switch
                {
                    PartitionRole.EfiSystem => (
                        "System",
                        SetupPartitionAction.PreserveSystem,
                        "KEEP — EFI BOOT",
                        "Required UEFI System Partition (GPT). Preserve this so your PC and SWBI remain bootable."),
                    PartitionRole.SystemReservedLegacy => (
                        "System (Active MBR)",
                        SetupPartitionAction.PreserveSystem,
                        "KEEP — ACTIVE MBR BOOT",
                        "Active Legacy BIOS System Reserved partition hosting bootmgr & BCD. Preserve this so your MBR disk stays bootable."),
                    PartitionRole.MicrosoftReserved => (
                        "MSR (Reserved)",
                        SetupPartitionAction.PreserveSystem,
                        "KEEP — MSR",
                        "Small Microsoft Reserved GPT partition (typically 16 MB). Leave untouched."),
                    PartitionRole.Recovery => (
                        "Recovery",
                        SetupPartitionAction.PreserveOther,
                        "KEEP (OR OPTIONAL)",
                        "Windows Recovery Environment partition. Keep intact unless intentionally consolidating adjacent space next to old Windows."),
                    _ => (
                        "Primary",
                        SetupPartitionAction.PreserveOther,
                        "KEEP — DATA / OTHER",
                        "Existing non-OS partition. Do not delete unless you intend to erase all data on it.")
                };

                string setupName = string.IsNullOrWhiteSpace(part.Label)
                    ? $"Disk {disk.DiskNumber} Partition {nextPartitionNumber}"
                    : $"Disk {disk.DiskNumber} Partition {nextPartitionNumber}: {part.Label}";

                rows.Add(new SetupPartitionRow
                {
                    DiskNumber = disk.DiskNumber,
                    PartitionNumber = nextPartitionNumber,
                    SetupName = setupName,
                    TypeLabel = typeLabel,
                    SizeBytes = part.SizeBytes,
                    Action = action,
                    ActionBadgeText = badge,
                    Explanation = explanation
                });
                nextPartitionNumber++;
            }
        }

        SetupPartitionRow? targetRow = rows.FirstOrDefault(r => r.Action == SetupPartitionAction.DeleteOrFormatTarget);
        SetupPartitionRow? installerRow = rows.FirstOrDefault(r => r.Action == SetupPartitionAction.PreserveInstallerSource);
        if (targetRow is null || installerRow is null)
        {
            return BuildFallbackGuide(env, sizingPlan, "The target or installer partition could not be mapped to a Windows Setup row.");
        }

        string bootRule = env.Firmware switch
        {
            FirmwareMode.LegacyBios => $"BOOT CHOOSER (legacy BIOS): Windows Boot Manager is requested to boot '{SwbiBrand.BootChooserEntryName}' once. Keep independent recovery media available.",
            FirmwareMode.Uefi => $"BOOT CHOOSER (UEFI): BootNext requests '{SwbiBrand.BootChooserEntryName}' once; it may also appear in the F12/F8 firmware menu. Firmware can ignore or hide it, so keep recovery media available.",
            _ => "BOOT CHOOSER: Firmware mode was not identified; do not proceed until it is verified."
        };

        string eraseRule = mustFormatInPlaceMbr
            ? $"TARGET TO FORMAT (MBR): Select '{targetRow.SetupName}' (~{targetRow.SizeDisplay}) and click FORMAT (do NOT click Delete, so the Active MBR flag & 4-slot primary table stay intact)."
            : $"TARGET TO ERASE: Select '{targetRow.SetupName}' (~{targetRow.SizeDisplay}) and click Delete (or Format).";

        string installTargetRule = mustFormatInPlaceMbr
            ? $"INSTALL TARGET: Keep the newly formatted '{targetRow.SetupName}' (~{targetRow.SizeDisplay}) selected and click Next."
            : $"INSTALL TARGET: After clicking Delete on the old Windows partition, select the resulting 'Disk {disk.DiskNumber} Unallocated Space' (~{targetRow.SizeDisplay}) and click Next.";

        var criticalRules = new[]
        {
            bootRule,
            "PARTITION CHOOSER: Drive letters (like C:) are NOT shown in Windows Setup's partition table — identify partitions by Disk #, Partition #, Label, and Size!",
            eraseRule,
            installTargetRule,
            $"DO NOT DELETE '{installerRow.SetupName}' (~{installerRow.SizeDisplay}, label '{SwbiBrand.PartitionVolumeLabel}') or any 'System' boot partition!",
            $"DRIVER CHOOSER ('Load driver'): Drivers auto-load from $WinPEDriver$, or click 'Load driver -> Browse -> {SwbiBrand.PartitionVolumeLabel} -> {SwbiBrand.DriverChooserFolderName}'.",
            $"After your new Windows boots to the desktop, run {SwbiBrand.BrandedTitle} in 'Post-Install Cleanup' mode (--cleanup) to remove '{SwbiBrand.PartitionVolumeLabel}' and reclaim disk space."
        };

        return new SetupPartitionGuide
        {
            TargetDiskNumber = disk.DiskNumber,
            TargetDiskModel = disk.FriendlyName,
            Rows = rows,
            CriticalRules = criticalRules
        };
    }

    public string FormatPlainTextCheatSheet(SetupPartitionGuide guide)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========================================================================");
        sb.AppendLine($"  {SwbiBrand.AsciiTitle.ToUpperInvariant()} — BOOT & SETUP GUIDE");
        sb.AppendLine("========================================================================");
        sb.AppendLine($"Target Physical Drive : Disk {guide.TargetDiskNumber} ({guide.TargetDiskModel})");
        sb.AppendLine($"Boot Chooser Entry    : {SwbiBrand.BootChooserEntryName}");
        sb.AppendLine($"Partition Volume Name : {SwbiBrand.PartitionVolumeLabel}");
        sb.AppendLine($"Load Driver Folder    : {SwbiBrand.PartitionVolumeLabel}:\\{SwbiBrand.DriverChooserFolderName}");
        sb.AppendLine($"Generated (UTC)       : {guide.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("Take a photo of this guide on your phone BEFORE restarting!");
        sb.AppendLine();
        sb.AppendLine("EXPECTED PARTITIONS IN WINDOWS SETUP ('Where do you want to install Windows?'):");
        sb.AppendLine("------------------------------------------------------------------------");

        foreach (SetupPartitionRow row in guide.Rows)
        {
            sb.AppendLine($"  [{row.ActionBadgeText}]");
            sb.AppendLine($"    Name : {row.SetupName}");
            sb.AppendLine($"    Size : {row.SizeDisplay}   |   Type : {row.TypeLabel}");
            sb.AppendLine($"    Note : {row.Explanation}");
            sb.AppendLine();
        }

        sb.AppendLine("CRITICAL RULES DURING BOOT & WINDOWS SETUP:");
        sb.AppendLine("------------------------------------------------------------------------");
        for (int i = 0; i < guide.CriticalRules.Count; i++)
        {
            sb.AppendLine($"  {i + 1}. {guide.CriticalRules[i]}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Produces a safe, label-based guide when the detailed per-partition rows cannot be built. Preparation
    /// must never be rolled back (which would delete a working installer partition) because of a formatting
    /// edge case in the cheat sheet.
    /// </summary>
    private static SetupPartitionGuide BuildFallbackGuide(
        SystemEnvironmentSnapshot env,
        PartitionSizingPlan sizingPlan,
        string reason)
    {
        return new SetupPartitionGuide
        {
            TargetDiskNumber = env.TargetDisk?.DiskNumber ?? 0,
            TargetDiskModel = env.TargetDisk?.FriendlyName ?? "Unknown disk",
            Rows = Array.Empty<SetupPartitionRow>(),
            CriticalRules = new[]
            {
                $"Identify partitions in Windows Setup by LABEL and SIZE, not by drive letter: {reason}",
                $"The installer partition is '{SwbiBrand.PartitionVolumeLabel}' (~{sizingPlan.InstallerSizeDisplay}). NEVER delete or format it — it is your only installation source.",
                $"The partition to erase is the old Windows system partition on Disk {env.TargetDisk?.DiskNumber.ToString() ?? "?"}. Keep every 'System', 'EFI', 'MSR' and 'Recovery' partition.",
                $"Boot entry requested by SWBI: '{SwbiBrand.BootChooserEntryName}' (one-time request). Keep independent recovery media available in case firmware ignores it.",
                $"After the new Windows boots, run {SwbiBrand.BrandedTitle} in Post-Install Cleanup mode (--cleanup) to remove the temporary partition and boot entry."
            }
        };
    }
}
