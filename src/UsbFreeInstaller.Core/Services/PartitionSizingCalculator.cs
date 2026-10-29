using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Services;

public sealed class PartitionSizingCalculator
{
    public const long DefaultOsSafetyReserveBytes = 8L * ByteFormatting.OneGb;
    private const long BaseHeadroomBytes = 1536L * ByteFormatting.OneMb; // 1.5 GB working safety buffer
    private const long SplitMetadataOverheadBytes = 256L * ByteFormatting.OneMb;
    private const long AlignmentMb = 64L;

    public PartitionSizingPlan Calculate(
        IsoInspectionResult iso,
        PartitionSnapshot? osPartition,
        ShrinkAnalysis? shrinkAnalysis,
        long driverReserveBytes = 256L * ByteFormatting.OneMb)
    {
        long swmChunkBytes = iso.SwmChunkSizeMb * ByteFormatting.OneMb;
        int estimatedSwmCount = iso.SplitStrategy is WimSplitStrategy.SplitWimToSwm or WimSplitStrategy.ConvertEsdThenSplitToSwm
            ? Math.Max(2, (int)Math.Ceiling(iso.PayloadSizeBytes / (double)swmChunkBytes))
            : 0;

        // Total ISO content + 3% FAT32 cluster/table overhead + SWM split header overhead + $WinPEDriver$ reserve + 1.5 GB safety headroom
        long safeDriverReserve = Math.Max(0L, driverReserveBytes);
        long fatOverheadBytes = (long)Math.Ceiling((iso.FileSizeBytes + safeDriverReserve) * 0.03);
        long splitExtraBytes = estimatedSwmCount > 0 ? SplitMetadataOverheadBytes : 0L;
        long rawRequiredBytes = iso.FileSizeBytes + safeDriverReserve + fatOverheadBytes + splitExtraBytes + BaseHeadroomBytes;

        long rawRequiredMb = (long)Math.Ceiling(rawRequiredBytes / (double)ByteFormatting.OneMb);
        long alignedMb = RoundUpToMultiple(
            Math.Max(PartitionSizingPlan.MinInstallerPartitionMb, rawRequiredMb),
            AlignmentMb);
        long installerBytes = alignedMb * ByteFormatting.OneMb;

        bool fitsFat32 = alignedMb <= PartitionSizingPlan.MaxNativeFat32PartitionMb;

        // Converting a >4 GB ESD requires temporary WIM staging space on C: prior to splitting
        long requiredTempOsBytes = iso.SplitStrategy == WimSplitStrategy.ConvertEsdThenSplitToSwm
            ? (long)Math.Ceiling(iso.PayloadSizeBytes * 1.40) + (2L * ByteFormatting.OneGb)
            : 1024L * ByteFormatting.OneMb;

        long osBefore = osPartition?.SizeBytes ?? shrinkAnalysis?.CurrentSizeBytes ?? 0L;
        long osAfter = Math.Max(0L, osBefore - installerBytes);

        long remainingOsFreeAfterShrink = shrinkAnalysis is not null
            ? Math.Max(0L, shrinkAnalysis.VolumeFreeBytes - installerBytes)
            : 0L;

        bool fitsShrink = false;
        string? blockingReason = null;

        if (!fitsFat32)
        {
            blockingReason =
                $"Calculated installer partition size ({alignedMb:N0} MB) exceeds SWBI's validated FAT32 installer-partition limit of {PartitionSizingPlan.MaxNativeFat32PartitionMb:N0} MB. " +
                "Use a smaller/trimmed ISO image, or split the ISO media so the extracted payload fits.";
        }
        else if (shrinkAnalysis is null || osPartition is null)
        {
            blockingReason = "Target OS partition shrink analysis is unavailable.";
        }
        else if (installerBytes > shrinkAnalysis.RawMaxShrinkableBytes)
        {
            blockingReason =
                $"Windows reports only {ByteFormatting.FormatGb(shrinkAnalysis.RawMaxShrinkableBytes)} can be shrunk on the OS partition, " +
                $"but {ByteFormatting.FormatGb(installerBytes)} is needed for the installer partition.";
        }
        else if (installerBytes > shrinkAnalysis.SafeUsableShrinkBytes)
        {
            blockingReason =
                $"Shrinking by {ByteFormatting.FormatGb(installerBytes)} would leave less than {ByteFormatting.FormatGb(shrinkAnalysis.OsSafetyReserveBytes)} " +
                "of free space on the running Windows partition. Free additional disk space first.";
        }
        else if (remainingOsFreeAfterShrink < requiredTempOsBytes)
        {
            blockingReason =
                $"Preparing this ISO requires {ByteFormatting.FormatGb(requiredTempOsBytes)} of temporary staging space on C: after shrinking, " +
                $"but only {ByteFormatting.FormatGb(remainingOsFreeAfterShrink)} would remain.";
        }
        else
        {
            fitsShrink = true;
        }

        return new PartitionSizingPlan
        {
            IsoSizeBytes = iso.FileSizeBytes,
            PayloadSizeBytes = iso.PayloadSizeBytes,
            DriverReserveBytes = safeDriverReserve,
            SplitStrategy = iso.SplitStrategy,
            EstimatedSwmCount = estimatedSwmCount,
            RequiredTempOsBytes = requiredTempOsBytes,
            InstallerPartitionBytes = installerBytes,
            InstallerPartitionMb = alignedMb,
            OsPartitionBeforeBytes = osBefore,
            OsPartitionAfterBytes = osAfter,
            RemainingOsFreeAfterShrinkBytes = remainingOsFreeAfterShrink,
            FitsWithinShrinkLimit = fitsShrink,
            FitsWithinFat32Limit = fitsFat32,
            BlockingReason = blockingReason
        };
    }

    private static long RoundUpToMultiple(long value, long multiple)
    {
        if (multiple <= 0)
        {
            return value;
        }

        long remainder = value % multiple;
        return remainder == 0 ? value : value + (multiple - remainder);
    }
}
