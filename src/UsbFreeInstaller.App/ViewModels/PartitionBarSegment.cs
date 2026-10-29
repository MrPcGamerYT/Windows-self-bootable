using System.Windows.Media;
using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.App.ViewModels;

public sealed class PartitionBarSegment
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Badge { get; init; }
    public required string PercentShareText { get; init; }
    public required Brush AccentBrush { get; init; }
    public required Brush SurfaceTintBrush { get; init; }
    public required double RelativeWidthPx { get; init; }
    public required double RibbonWidthPx { get; init; }
}

public static class PartitionMapBuilder
{
    private static readonly SolidColorBrush EfiBrush = Frozen(0x14, 0xB8, 0xA6);         // Vibrant Teal
    private static readonly SolidColorBrush EfiSurface = FrozenArgb(0x28, 0x14, 0xB8, 0xA6);

    private static readonly SolidColorBrush MsrBrush = Frozen(0x64, 0x74, 0x8B);         // Slate
    private static readonly SolidColorBrush MsrSurface = FrozenArgb(0x24, 0x64, 0x74, 0x8B);

    private static readonly SolidColorBrush OsBrush = Frozen(0x3B, 0x82, 0xF6);          // Electric Royal Blue
    private static readonly SolidColorBrush OsSurface = FrozenArgb(0x2A, 0x3B, 0x82, 0xF6);

    private static readonly SolidColorBrush InstallerBrush = Frozen(0x8B, 0x5C, 0xF6);   // Vivid Violet
    private static readonly SolidColorBrush InstallerSurface = FrozenArgb(0x2E, 0x8B, 0x5C, 0xF6);

    private static readonly SolidColorBrush UnallocatedBrush = Frozen(0x10, 0xB9, 0x81); // Emerald
    private static readonly SolidColorBrush UnallocatedSurface = FrozenArgb(0x2C, 0x10, 0xB9, 0x81);

    private static readonly SolidColorBrush RecoveryBrush = Frozen(0x0E, 0xA5, 0xE9);    // Sky Slate
    private static readonly SolidColorBrush RecoverySurface = FrozenArgb(0x22, 0x0E, 0xA5, 0xE9);

    public static IReadOnlyList<PartitionBarSegment> BuildBeforeMap(SystemEnvironmentSnapshot env)
    {
        DiskSnapshot? disk = env.TargetDisk;
        if (disk is null || disk.Partitions.Count == 0)
        {
            return Array.Empty<PartitionBarSegment>();
        }

        double total = Math.Max(1.0, disk.SizeBytes);
        return disk.Partitions
            .OrderBy(p => p.OffsetBytes)
            .Select(p => new PartitionBarSegment
            {
                Title = FormatTitle(p),
                Subtitle = p.SizeDisplay,
                Badge = p.Role switch
                {
                    PartitionRole.EfiSystem => "EFI SYSTEM",
                    PartitionRole.MicrosoftReserved => "MSR",
                    PartitionRole.WindowsOs => "ACTIVE OS (C:)",
                    PartitionRole.Recovery => "WINRE",
                    PartitionRole.TemporaryInstaller => SwbiBrand.PartitionVolumeLabel,
                    _ => string.IsNullOrWhiteSpace(p.FileSystem) ? "PARTITION" : p.FileSystem.ToUpperInvariant()
                },
                PercentShareText = FormatPercent(p.SizeBytes, total),
                AccentBrush = BrushForRole(p.Role),
                SurfaceTintBrush = SurfaceForRole(p.Role),
                RelativeWidthPx = ComputeDisplayWidth(p.SizeBytes, total),
                RibbonWidthPx = ComputeRibbonWidth(p.SizeBytes, total)
            })
            .ToArray();
    }

    public static IReadOnlyList<PartitionBarSegment> BuildAfterPrepMap(
        SystemEnvironmentSnapshot env,
        PartitionSizingPlan? plan)
    {
        DiskSnapshot? disk = env.TargetDisk;
        if (disk is null || plan is null)
        {
            return Array.Empty<PartitionBarSegment>();
        }

        double total = Math.Max(1.0, disk.SizeBytes);
        var list = new List<PartitionBarSegment>();

        foreach (PartitionSnapshot p in disk.Partitions.OrderBy(p => p.OffsetBytes))
        {
            if (p.PartitionNumber == env.TargetOsPartitionNumber)
            {
                list.Add(new PartitionBarSegment
                {
                    Title = $"Old Windows ({env.SystemDriveLetter}:)",
                    Subtitle = plan.OsAfterDisplay,
                    Badge = "SHRUNK OS",
                    PercentShareText = FormatPercent(plan.OsPartitionAfterBytes, total),
                    AccentBrush = OsBrush,
                    SurfaceTintBrush = OsSurface,
                    RelativeWidthPx = ComputeDisplayWidth(plan.OsPartitionAfterBytes, total),
                    RibbonWidthPx = ComputeRibbonWidth(plan.OsPartitionAfterBytes, total)
                });
                list.Add(new PartitionBarSegment
                {
                    Title = InstallerManifest.DefaultVolumeLabel,
                    Subtitle = plan.InstallerSizeDisplay,
                    Badge = "FAT32 BOOT SOURCE",
                    PercentShareText = FormatPercent(plan.InstallerPartitionBytes, total),
                    AccentBrush = InstallerBrush,
                    SurfaceTintBrush = InstallerSurface,
                    RelativeWidthPx = ComputeDisplayWidth(plan.InstallerPartitionBytes, total),
                    RibbonWidthPx = ComputeRibbonWidth(plan.InstallerPartitionBytes, total)
                });
            }
            else
            {
                list.Add(new PartitionBarSegment
                {
                    Title = FormatTitle(p),
                    Subtitle = p.SizeDisplay,
                    Badge = p.Role == PartitionRole.EfiSystem ? "PRESERVE EFI" : "KEEP",
                    PercentShareText = FormatPercent(p.SizeBytes, total),
                    AccentBrush = BrushForRole(p.Role),
                    SurfaceTintBrush = SurfaceForRole(p.Role),
                    RelativeWidthPx = ComputeDisplayWidth(p.SizeBytes, total),
                    RibbonWidthPx = ComputeRibbonWidth(p.SizeBytes, total)
                });
            }
        }

        return list;
    }

    public static IReadOnlyList<PartitionBarSegment> BuildSetupDeletedMap(
        SystemEnvironmentSnapshot env,
        PartitionSizingPlan? plan)
    {
        DiskSnapshot? disk = env.TargetDisk;
        if (disk is null || plan is null)
        {
            return Array.Empty<PartitionBarSegment>();
        }

        double total = Math.Max(1.0, disk.SizeBytes);
        var list = new List<PartitionBarSegment>();

        foreach (PartitionSnapshot p in disk.Partitions.OrderBy(p => p.OffsetBytes))
        {
            if (p.PartitionNumber == env.TargetOsPartitionNumber)
            {
                list.Add(new PartitionBarSegment
                {
                    Title = "Unallocated Space",
                    Subtitle = plan.OsAfterDisplay,
                    Badge = "SELECT & INSTALL HERE",
                    PercentShareText = FormatPercent(plan.OsPartitionAfterBytes, total),
                    AccentBrush = UnallocatedBrush,
                    SurfaceTintBrush = UnallocatedSurface,
                    RelativeWidthPx = ComputeDisplayWidth(plan.OsPartitionAfterBytes, total),
                    RibbonWidthPx = ComputeRibbonWidth(plan.OsPartitionAfterBytes, total)
                });
                list.Add(new PartitionBarSegment
                {
                    Title = InstallerManifest.DefaultVolumeLabel,
                    Subtitle = plan.InstallerSizeDisplay,
                    Badge = "DO NOT DELETE",
                    PercentShareText = FormatPercent(plan.InstallerPartitionBytes, total),
                    AccentBrush = InstallerBrush,
                    SurfaceTintBrush = InstallerSurface,
                    RelativeWidthPx = ComputeDisplayWidth(plan.InstallerPartitionBytes, total),
                    RibbonWidthPx = ComputeRibbonWidth(plan.InstallerPartitionBytes, total)
                });
            }
            else
            {
                list.Add(new PartitionBarSegment
                {
                    Title = FormatTitle(p),
                    Subtitle = p.SizeDisplay,
                    Badge = "DO NOT DELETE",
                    PercentShareText = FormatPercent(p.SizeBytes, total),
                    AccentBrush = BrushForRole(p.Role),
                    SurfaceTintBrush = SurfaceForRole(p.Role),
                    RelativeWidthPx = ComputeDisplayWidth(p.SizeBytes, total),
                    RibbonWidthPx = ComputeRibbonWidth(p.SizeBytes, total)
                });
            }
        }

        return list;
    }

    private static string FormatTitle(PartitionSnapshot p) =>
        p.Role switch
        {
            PartitionRole.EfiSystem => "EFI System",
            PartitionRole.MicrosoftReserved => "MSR",
            PartitionRole.WindowsOs => string.IsNullOrWhiteSpace(p.DriveLetter) ? "Windows" : $"Windows ({p.DriveLetter}:)",
            PartitionRole.Recovery => "Recovery",
            PartitionRole.TemporaryInstaller => InstallerManifest.DefaultVolumeLabel,
            _ => string.IsNullOrWhiteSpace(p.Label) ? $"Part {p.PartitionNumber}" : p.Label
        };

    private static string FormatPercent(long sizeBytes, double totalBytes)
    {
        double pct = Math.Clamp((sizeBytes / totalBytes) * 100.0, 0.0, 100.0);
        return pct < 1.0 ? "< 1%" : $"{pct:0.#}%";
    }

    private static Brush BrushForRole(PartitionRole role) =>
        role switch
        {
            PartitionRole.EfiSystem => EfiBrush,
            PartitionRole.MicrosoftReserved => MsrBrush,
            PartitionRole.WindowsOs => OsBrush,
            PartitionRole.TemporaryInstaller => InstallerBrush,
            PartitionRole.Recovery => RecoveryBrush,
            _ => MsrBrush
        };

    private static Brush SurfaceForRole(PartitionRole role) =>
        role switch
        {
            PartitionRole.EfiSystem => EfiSurface,
            PartitionRole.MicrosoftReserved => MsrSurface,
            PartitionRole.WindowsOs => OsSurface,
            PartitionRole.TemporaryInstaller => InstallerSurface,
            PartitionRole.Recovery => RecoverySurface,
            _ => MsrSurface
        };

    private static double ComputeDisplayWidth(long sizeBytes, double totalBytes)
    {
        double ratio = Math.Clamp(sizeBytes / totalBytes, 0.0, 1.0);
        return Math.Round(140.0 + (ratio * 340.0));
    }

    private static double ComputeRibbonWidth(long sizeBytes, double totalBytes)
    {
        double ratio = Math.Clamp(sizeBytes / totalBytes, 0.0, 1.0);
        return Math.Round(36.0 + (ratio * 640.0));
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush FrozenArgb(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
