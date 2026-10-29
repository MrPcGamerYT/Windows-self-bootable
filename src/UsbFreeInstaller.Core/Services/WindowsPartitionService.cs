using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services.Native;

namespace UsbFreeInstaller.Core.Services;

/// <summary>
/// 100% Self-Contained Native Win32 Kernel Partition & FAT32 Volume Engine.
/// Performs live NTFS tail evacuation & shrinking (FSCTL_MOVE_FILE + FSCTL_SHRINK_VOLUME + IOCTL_DISK_GROW_PARTITION),
/// GPT/MBR partition table updates (IOCTL_DISK_SET_DRIVE_LAYOUT_EX), MountMgr drive-letter mounting
/// (IOCTL_VOLUME_ONLINE + SetVolumeMountPointW), and pure C# FAT32 formatting (NativeFat32Formatter)
/// with ZERO reliance on powershell.exe, diskpart.exe, format.com, or $env:TEMP.
/// </summary>
public sealed class WindowsPartitionService : IPartitionService
{
    private const uint IOCTL_VOLUME_ONLINE = 0x0056C008;
    private const byte MBR_PARTITION_FAT32_LBA = 0x0C;
    private const byte MBR_PARTITION_UNUSED = 0x00;

    private static readonly char[] PreferredInstallerDriveLetters =
    {
        'W', 'V', 'U', 'T', 'S', 'R', 'Q', 'P', 'O', 'N', 'M', 'L', 'K', 'J', 'I', 'H', 'Z', 'Y', 'X'
    };

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IProcessRunner _processRunner;
    private readonly ISafeLogger _logger;

    public WindowsPartitionService(IProcessRunner processRunner, ISafeLogger logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public Task SuspendBitLockerForNextRebootAsync(
        string volumeLetter,
        CancellationToken cancellationToken = default)
    {
        string cleanLetter = volumeLetter.TrimEnd(':', '\\', '/').ToUpperInvariant();
        _logger.Info("BitLocker", $"Suspending BitLocker protectors in-process on {cleanLetter}: (zero manage-bde.exe)...");

        if (TrySuspendBitLockerInProcess(cleanLetter, out string detail))
        {
            _logger.Info("BitLocker", detail);
        }
        else
        {
            _logger.Warn("BitLocker", $"{detail} Ensure your BitLocker recovery key for {cleanLetter}: is backed up if Device Encryption is active.");
        }

        return Task.CompletedTask;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int FveOpenVolumeWDelegate(string pwzVolume, [MarshalAs(UnmanagedType.Bool)] bool bNeedWriteAccess, out IntPtr phVolume);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FveDisableAuthenticationWDelegate(IntPtr hVolume);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FveCommitChangesDelegate(IntPtr hVolume);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FveCloseVolumeDelegate(IntPtr hVolume);

    private static bool TrySuspendBitLockerInProcess(string driveLetter, out string detail)
    {
        if (!OperatingSystem.IsWindows())
        {
            detail = "Non-Windows runtime.";
            return false;
        }

        string fvePath = Path.Combine(Environment.SystemDirectory, "fveapi.dll");
        if (!File.Exists(fvePath) || !NativeLibrary.TryLoad(fvePath, out IntPtr hFve) || hFve == IntPtr.Zero)
        {
            detail = "fveapi.dll is not present on this stripped/Lite Windows build.";
            return false;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(hFve, "FveOpenVolumeW", out IntPtr pOpen) ||
                !NativeLibrary.TryGetExport(hFve, "FveDisableAuthenticationW", out IntPtr pDisable) ||
                !NativeLibrary.TryGetExport(hFve, "FveCommitChanges", out IntPtr pCommit) ||
                !NativeLibrary.TryGetExport(hFve, "FveCloseVolume", out IntPtr pClose))
            {
                detail = "In-process fveapi exports differ on this Windows build.";
                return false;
            }

            var fveOpen = Marshal.GetDelegateForFunctionPointer<FveOpenVolumeWDelegate>(pOpen);
            var fveDisable = Marshal.GetDelegateForFunctionPointer<FveDisableAuthenticationWDelegate>(pDisable);
            var fveCommit = Marshal.GetDelegateForFunctionPointer<FveCommitChangesDelegate>(pCommit);
            var fveClose = Marshal.GetDelegateForFunctionPointer<FveCloseVolumeDelegate>(pClose);

            var volGuidSb = new StringBuilder(128);
            string volTarget = Win32StorageInterop.GetVolumeNameForVolumeMountPointW($"{driveLetter}:\\", volGuidSb, (uint)volGuidSb.Capacity)
                ? volGuidSb.ToString().TrimEnd('\\')
                : $@"\\.\{driveLetter}:";

            int hr = fveOpen(volTarget, true, out IntPtr hVol);
            if (hr < 0 || hVol == IntPtr.Zero)
            {
                detail = $"fveapi FveOpenVolumeW returned 0x{hr:X8}.";
                return false;
            }

            try
            {
                int hrDis = fveDisable(hVol);
                int hrCom = hrDis >= 0 ? fveCommit(hVol) : hrDis;
                if (hrCom >= 0)
                {
                    detail = $"Suspended BitLocker protectors on {driveLetter}: in-process via fveapi.dll.";
                    return true;
                }

                detail = $"fveapi suspend returned 0x{hrCom:X8}.";
                return false;
            }
            finally
            {
                fveClose(hVol);
            }
        }
        catch (Exception ex)
        {
            detail = $"In-process BitLocker check skipped ({ex.Message}).";
            return false;
        }
    }

    public Task OptimizeOsVolumeBeforeShrinkAsync(
        string volumeLetter,
        StorageDriveKind driveKind,
        CancellationToken cancellationToken = default)
    {
        string cleanLetter = volumeLetter.TrimEnd(':', '\\', '/').ToUpperInvariant();

        // Tail-cluster evacuation is performed inside the shrink transaction itself
        // (NativeNtfsShrinkEngine.ShrinkOnlinePartition: FSCTL_GET_VOLUME_BITMAP + FSCTL_MOVE_FILE), so there is
        // no separate pre-pass to run here. Report that honestly instead of claiming a completed optimization.
        _logger.Info(
            "DriveTune",
            $"{cleanLetter}: ({driveKind}) needs no separate pre-shrink pass; movable tail clusters are evacuated " +
            "inside the shrink transaction via native FSCTL_MOVE_FILE, and unmovable clusters are reported before resizing.");
        return Task.CompletedTask;
    }

    public Task<CreatedInstallerPartitionInfo> ShrinkOsAndCreateFat32InstallerPartitionAsync(
        int diskNumber,
        int osPartitionNumber,
        string expectedOsPartitionGuid,
        long newOsPartitionSizeBytes,
        long installerPartitionSizeBytes,
        string volumeLabel,
        CancellationToken cancellationToken = default)
    {
        string sysDriveRaw = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        return ShrinkVolumeAndCreateFat32InstallerPartitionAsync(
            diskNumber,
            osPartitionNumber,
            sysDriveRaw,
            expectedOsPartitionGuid,
            newOsPartitionSizeBytes,
            installerPartitionSizeBytes,
            volumeLabel,
            cancellationToken);
    }

    public Task<CreatedInstallerPartitionInfo> ShrinkVolumeAndCreateFat32InstallerPartitionAsync(
        int diskNumber,
        int osPartitionNumber,
        string osDriveLetter,
        string expectedOsPartitionGuid,
        long newOsPartitionSizeBytes,
        long installerPartitionSizeBytes,
        string volumeLabel,
        CancellationToken cancellationToken = default)
    {
        if (installerPartitionSizeBytes > PartitionSizingPlan.MaxNativeFat32PartitionMb * ByteFormatting.OneMb)
        {
            throw new InvalidOperationException("Requested installer partition exceeds 32 GB FAT32 formatting limit.");
        }

        return Task.Run(
            () => ShrinkAndCreateNativeCore(
                diskNumber,
                osPartitionNumber,
                osDriveLetter,
                expectedOsPartitionGuid,
                newOsPartitionSizeBytes,
                installerPartitionSizeBytes,
                volumeLabel,
                cancellationToken),
            cancellationToken);
    }

    private CreatedInstallerPartitionInfo ShrinkAndCreateNativeCore(
        int diskNumber,
        int osPartitionNumber,
        string sysDriveRaw,
        string expectedOsPartitionGuid,
        long newOsPartitionSizeBytes,
        long installerPartitionSizeBytes,
        string volumeLabel,
        CancellationToken cancellationToken)
    {
        string sysDriveLetter = sysDriveRaw.TrimEnd(':', '\\', '/').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(sysDriveLetter))
        {
            sysDriveLetter = "C";
        }

        _logger.Info(
            "Partition",
            $"Native Win32: Re-validating Disk {diskNumber} Partition {osPartitionNumber} before shrinking {sysDriveLetter}: to {ByteFormatting.FormatGb(newOsPartitionSizeBytes)}...");

        long sysStartOffsetFromVol = QueryVolumeStartOffsetOrZero(sysDriveLetter);
        long diskTotalLengthBytes;
        RawDiskLayoutSnapshot initialLayout;

        using (SafeFileHandle diskHandle = OpenPhysicalDriveReadWrite(diskNumber))
        {
            diskTotalLengthBytes = Win32StorageInterop.TryQueryDeviceLengthBytesOrZero(diskHandle);
            initialLayout = Win32StorageInterop.TryReadDiskLayout(diskHandle)
                ?? throw new InvalidOperationException($"Unable to read partition table on PhysicalDrive{diskNumber}.");
        }

        if (initialLayout.PartitionStyle != Win32StorageInterop.PARTITION_STYLE_GPT &&
            initialLayout.PartitionStyle != Win32StorageInterop.PARTITION_STYLE_MBR)
        {
            throw new InvalidOperationException(
                $"Safety check failed: Disk {diskNumber} uses unsupported partition style ({initialLayout.PartitionStyle}).");
        }

        int usedMbrPrimarySlots = initialLayout.MbrPrimarySlotsUsed > 0
            ? initialLayout.MbrPrimarySlotsUsed
            : initialLayout.Partitions.Count(p => p.RawSlotIndex is >= 0 and < 4 || p.RawSlotIndex < 0);

        if (initialLayout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR &&
            usedMbrPrimarySlots >= 4)
        {
            throw new InvalidOperationException(
                $"Safety check failed: MBR Disk {diskNumber} already uses all 4 primary MBR table slots ({usedMbrPrimarySlots}/4).");
        }

        RawPartitionLayoutEntry osPart = FindOsPartitionInLayout(initialLayout, osPartitionNumber, sysStartOffsetFromVol)
            ?? throw new InvalidOperationException(
                $"Safety check failed: Could not locate OS partition #{osPartitionNumber} ({sysDriveLetter}:) on Disk {diskNumber}.");

        if (initialLayout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT &&
            !string.IsNullOrWhiteSpace(expectedOsPartitionGuid) &&
            osPart.GptPartitionId != Guid.Empty)
        {
            string currentGuidStr = $"{{{osPart.GptPartitionId:D}}}";
            if (!string.Equals(currentGuidStr, expectedOsPartitionGuid, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(osPart.GptPartitionId.ToString("D"), expectedOsPartitionGuid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Safety check failed: OS partition GUID changed from {expectedOsPartitionGuid} to {currentGuidStr}.");
            }
        }

        // Snapshot existing volumes BEFORE creating the new partition so we can pinpoint the newly created volume
        HashSet<string> volumesBefore = Win32StorageInterop.EnumerateAllVolumes()
            .Select(v => v.VolumeGuidPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Step 1: Shrink live C: volume + underlying OS partition natively in-process
        if (osPart.PartitionLengthBytes > newOsPartitionSizeBytes + (2L * 1024 * 1024))
        {
            NativeNtfsShrinkEngine.ShrinkOnlinePartition(
                diskNumber,
                osPart.PartitionNumber,
                sysDriveLetter,
                newOsPartitionSizeBytes,
                _logger);
        }

        // Step 2: Re-read disk layout and compute exact 1 MiB-aligned unallocated span for SWBI-SETUP
        Guid createdGuid = Guid.NewGuid();
        long newPartStartOffset;
        long newPartLengthBytes;
        int createdPartNumber;
        RawPartitionLayoutEntry verifiedOsPart;
        uint committedMbrSignature = initialLayout.MbrSignature;
        int logicalSectorBytes = 512;

        // Temporarily suppress Windows MountPointManager auto-drive-letter assignment while the new partition is still
        // unformatted raw bytes — prevents Explorer from popping up "You need to format the disk in drive E: before you can use it"!
        string volumeGuidWithSlash;
        using (MountMgrAutoMountSuppressionScope.TryEnter(_logger))
        {
            using (SafeFileHandle diskHandle = OpenPhysicalDriveReadWrite(diskNumber))
            {
                if (!Win32StorageInterop.TryQueryLogicalSectorSize(diskHandle, out logicalSectorBytes))
                    throw new InvalidOperationException("Cannot verify the disk's logical sector size before creating a partition.");
                RawDiskLayoutSnapshot postShrinkLayout = Win32StorageInterop.TryReadDiskLayout(diskHandle)
                    ?? throw new InvalidOperationException($"Unable to re-read partition layout on Disk {diskNumber} after shrink.");

                committedMbrSignature = postShrinkLayout.MbrSignature;

                if (diskTotalLengthBytes <= 0)
                {
                    diskTotalLengthBytes = Win32StorageInterop.TryQueryDeviceLengthBytesOrZero(diskHandle);
                }

                RawPartitionLayoutEntry refreshedOsPart = postShrinkLayout.Partitions.FirstOrDefault(p =>
                    PartitionLayoutSafety.IsSamePartition(osPart, p))
                    ?? throw new InvalidOperationException("The original OS partition could not be re-identified after shrink.");

                if (postShrinkLayout.PartitionStyle != initialLayout.PartitionStyle ||
                    (postShrinkLayout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT && postShrinkLayout.GptDiskId != initialLayout.GptDiskId) ||
                    (postShrinkLayout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR && postShrinkLayout.MbrSignature != initialLayout.MbrSignature))
                {
                    throw new InvalidOperationException("Disk identity changed after shrink; refusing to create an installer partition.");
                }

                // Never allocate or format space until the actual OS partition and NTFS $Boot agree.
                NativeNtfsShrinkEngine.VerifyNtfsFitsPartition(sysDriveLetter, diskNumber, refreshedOsPart);
                verifiedOsPart = refreshedOsPart;

                (newPartStartOffset, newPartLengthBytes) = LocateAlignedGapForNewPartition(
                    postShrinkLayout,
                    refreshedOsPart,
                    diskTotalLengthBytes,
                    installerPartitionSizeBytes,
                    logicalSectorBytes);

                uint hiddenSectors = (uint)Math.Min(uint.MaxValue, newPartStartOffset / Math.Max(512, logicalSectorBytes));
                var newMbrCandidate = new RawPartitionLayoutEntry(
                    PartitionStyle: postShrinkLayout.PartitionStyle,
                    StartingOffsetBytes: newPartStartOffset,
                    PartitionLengthBytes: newPartLengthBytes,
                    PartitionNumber: 0,
                    GptPartitionType: Win32StorageInterop.GptBasicDataTypeGuid,
                    GptPartitionId: createdGuid,
                    GptAttributes: Win32StorageInterop.GPT_BASIC_DATA_ATTRIBUTE_NO_DRIVE_LETTER,
                    GptName: "SWBI - Windows Bootable Setup",
                    MbrPartitionType: MBR_PARTITION_FAT32_LBA,
                    MbrBootIndicator: false,
                    MbrRecognized: true,
                    MbrHiddenSectors: hiddenSectors,
                    MbrPartitionId: createdGuid);

                byte[]? mbrPatchedBytes = postShrinkLayout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR
                    ? Win32StorageInterop.TrySerializeMbrSlotPatchPreservingEbrs(postShrinkLayout, newMbrCandidate, primarySlotIndexToDelete: null)
                    : null;

                if (mbrPatchedBytes is not null)
                {
                    createdPartNumber = postShrinkLayout.Partitions.Count(p => !p.IsMbrExtendedContainer) + 1;
                    CommitRawDiskLayoutBytesNative(diskHandle, mbrPatchedBytes);
                }
                else
                {
                    List<(RawPartitionLayoutEntry Entry, bool Rewrite)> updatedEntries = BuildPartitionEntriesWithNewSwbiPartition(
                        postShrinkLayout,
                        newPartStartOffset,
                        newPartLengthBytes,
                        createdGuid,
                        Win32StorageInterop.GPT_BASIC_DATA_ATTRIBUTE_NO_DRIVE_LETTER,
                        out createdPartNumber);

                    CommitDiskLayoutNative(diskHandle, postShrinkLayout, updatedEntries);
                }

                // Require the exact newly written partition, not a nearby or unrelated entry.
                RawDiskLayoutSnapshot committed = Win32StorageInterop.TryReadDiskLayout(diskHandle)
                    ?? throw new InvalidOperationException("Cannot verify the created partition; an unformatted partition may remain.");
                RawPartitionLayoutEntry[] newEntries = committed.Partitions.Where(p =>
                    !p.IsMbrExtendedContainer && p.StartingOffsetBytes == newPartStartOffset).ToArray();
                if (newEntries.Length != 1 || newEntries[0].PartitionLengthBytes != newPartLengthBytes ||
                    newEntries[0].PartitionNumber <= 0 ||
                    (committed.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT && newEntries[0].GptPartitionId != createdGuid))
                    throw new InvalidOperationException("The created partition could not be verified; it was not formatted.");
                createdPartNumber = newEntries[0].PartitionNumber;
            }

            _logger.Info(
                "Partition",
                $"Native IOCTL_DISK_SET_DRIVE_LAYOUT_EX created Partition {createdPartNumber} at offset {newPartStartOffset} ({ByteFormatting.FormatGb(newPartLengthBytes)}). Waiting for Volume Manager...");

            // Step 3: Wait for Windows volmgr.sys to surface \\?\Volume{guid}\ for the new partition & online it
            string volumeGuidPath = WaitForNewPartitionVolumeGuidPath(
                diskNumber,
                newPartStartOffset,
                volumesBefore,
                cancellationToken);

            volumeGuidWithSlash = volumeGuidPath.EndsWith('\\') ? volumeGuidPath : volumeGuidPath + "\\";
            string volumeDevicePathNoSlash = volumeGuidWithSlash.TrimEnd('\\');
            BringVolumeOnlineIfNeeded(volumeDevicePathNoSlash);

            // Re-check both the OS and the exact new volume immediately before formatting.
            // If any check fails, leave the unformatted partition for manual inspection.
            using (SafeFileHandle disk = OpenPhysicalDriveReadWrite(diskNumber))
            using (SafeFileHandle volume = Win32StorageInterop.CreateFileW(
                volumeDevicePathNoSlash, 0,
                Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
                IntPtr.Zero, Win32StorageInterop.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                RawDiskLayoutSnapshot latest = Win32StorageInterop.TryReadDiskLayout(disk)
                    ?? throw new InvalidOperationException("Cannot re-check partition geometry before formatting; an unformatted partition may remain.");
                if (volume.IsInvalid || !Win32StorageInterop.TryGetSingleVolumeDiskExtent(volume,
                    out int actualDisk, out long actualOffset, out long actualLength))
                    throw new InvalidOperationException("Cannot identify the new volume before formatting; an unformatted partition may remain.");
                RawPartitionLayoutEntry actualOs = PartitionLayoutSafety.VerifyBeforeFormatting(
                    initialLayout, verifiedOsPart, latest, diskNumber, newPartStartOffset, newPartLengthBytes,
                    createdGuid, actualDisk, actualOffset, actualLength);
                NativeNtfsShrinkEngine.VerifyNtfsFitsPartition(sysDriveLetter, diskNumber, actualOs);
            }

            // Step 4: Format the raw \\?\Volume{guid} kernel device directly as FAT32 in pure C# BEFORE mounting any drive letter!
            _logger.Info(
                "Partition",
                $"Formatting {volumeDevicePathNoSlash} as FAT32 ('{volumeLabel}') using pure C# NativeFat32Formatter...");

            NativeFat32Formatter.FormatVolumeDevicePath(
                volumeDevicePathNoSlash,
                volumeLabel,
                newPartStartOffset,
                expectedDiskNumber: diskNumber,
                expectedVolumeLengthBytes: newPartLengthBytes,
                expectedSectorBytes: logicalSectorBytes);

            // Clear GPT NO_DRIVE_LETTER attribute now that the volume has a 100% valid FAT32 filesystem
            if (initialLayout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT)
            {
                ClearGptNoDriveLetterAttributeBestEffort(diskNumber, createdGuid, newPartStartOffset);
            }
        }

        // Step 5: Ensure a clean drive letter (e.g. W:) is mounted to the already-formatted FAT32 volume
        string assignedLetter = EnsureDriveLetterMounted(volumeGuidWithSlash);

        // Refresh directory visibility after format/mount
        for (int attempt = 0; attempt < 25; attempt++)
        {
            if (Directory.Exists($"{assignedLetter}:\\"))
            {
                break;
            }
            Thread.Sleep(200);
        }

        if (!Directory.Exists($"{assignedLetter}:\\"))
        {
            Win32StorageInterop.SetVolumeMountPointW($"{assignedLetter}:\\", volumeGuidWithSlash);
            Thread.Sleep(300);
        }

        if (!Directory.Exists($"{assignedLetter}:\\"))
        {
            throw new IOException($"Formatted FAT32 volume {assignedLetter}:\\ did not mount cleanly in Windows Explorer namespace.");
        }

        string guidFormatted = initialLayout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT
            ? $"{{{createdGuid:D}}}"
            : $"MBR:{committedMbrSignature:X8}:{newPartStartOffset:X12}";

        var info = new CreatedInstallerPartitionInfo(
            DiskNumber: diskNumber,
            PartitionNumber: createdPartNumber,
            PartitionGuid: guidFormatted,
            DriveLetter: assignedLetter,
            OffsetBytes: newPartStartOffset,
            SizeBytes: newPartLengthBytes);

        _logger.Info(
            "Partition",
            $"Created self-contained FAT32 partition: Disk {info.DiskNumber} Partition {info.PartitionNumber} ({info.DriveLetter}:, {ByteFormatting.FormatGb(info.SizeBytes)}).");

        return info;
    }

    public async Task WriteInstallerManifestAsync(
        string installerDriveLetter,
        InstallerManifest manifest,
        CancellationToken cancellationToken = default)
    {
        string cleanLetter = installerDriveLetter.TrimEnd(':', '\\', '/');
        string rootDir = $"{cleanLetter}:{Path.DirectorySeparatorChar}";
        string manifestPath = Path.Combine(rootDir, InstallerManifest.ManifestFileName);

        string json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        string tempPath = manifestPath + ".new";

        // The manifest is the only record of what SWBI owns on this partition, and the firmware entry is
        // already armed when it is written. Write, flush to the FAT32 volume, then rename atomically so an
        // interruption can never leave a truncated or half-written manifest behind.
        byte[] payload = Encoding.UTF8.GetBytes(json);
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, manifestPath, overwrite: true);
        _logger.Info("Manifest", $"Wrote installer verification manifest to {manifestPath} ({payload.Length} bytes, flushed to disk).");
    }

    public Task DeleteInstallerPartitionAsync(
        int diskNumber,
        int installerPartitionNumber,
        string expectedPartitionGuid,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => DeleteInstallerPartitionNativeCore(diskNumber, installerPartitionNumber, expectedPartitionGuid),
            cancellationToken);
    }

    private void DeleteInstallerPartitionNativeCore(
        int diskNumber,
        int installerPartitionNumber,
        string expectedPartitionGuid)
    {
        _logger.Info(
            "Cleanup",
            $"Native Win32: Re-verifying Disk {diskNumber} Partition {installerPartitionNumber} before deletion...");

        string sysDriveRaw = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        string sysDriveLetter = sysDriveRaw.TrimEnd(':', '\\', '/').ToUpperInvariant();
        long sysStartOffset = QueryVolumeStartOffsetOrZero(sysDriveLetter);

        using SafeFileHandle diskHandle = OpenPhysicalDriveReadWrite(diskNumber);
        RawDiskLayoutSnapshot layout = Win32StorageInterop.TryReadDiskLayout(diskHandle)
            ?? throw new InvalidOperationException($"Unable to read partition layout on Disk {diskNumber}.");

        RawPartitionLayoutEntry target = layout.Partitions.FirstOrDefault(p =>
                !p.IsMbrExtendedContainer &&
                ((!string.IsNullOrWhiteSpace(expectedPartitionGuid) &&
                  p.GptPartitionId != Guid.Empty &&
                  string.Equals($"{{{p.GptPartitionId:D}}}", expectedPartitionGuid, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrWhiteSpace(expectedPartitionGuid) &&
                  layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR &&
                  string.Equals($"MBR:{layout.MbrSignature:X8}:{p.StartingOffsetBytes:X12}", expectedPartitionGuid, StringComparison.OrdinalIgnoreCase)) ||
                 p.PartitionNumber == installerPartitionNumber))
            ?? throw new InvalidOperationException(
                $"Could not find temporary installer partition #{installerPartitionNumber} on Disk {diskNumber}.");

        if (sysStartOffset > 0 && Math.Abs(target.StartingOffsetBytes - sysStartOffset) < 65536)
        {
            throw new InvalidOperationException("Safety check failed: Refusing to delete active Windows OS partition!");
        }

        if (layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT &&
            target.GptPartitionType == Win32StorageInterop.GptEfiSystemTypeGuid)
        {
            throw new InvalidOperationException("Safety check failed: Refusing to delete EFI System Partition (ESP)!");
        }

        if (layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR &&
            target.MbrBootIndicator)
        {
            throw new InvalidOperationException("Safety check failed: Refusing to delete the Active (0x80) Legacy BIOS boot partition!");
        }

        if (layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT &&
            !string.IsNullOrWhiteSpace(expectedPartitionGuid) &&
            target.GptPartitionId != Guid.Empty &&
            !string.Equals($"{{{target.GptPartitionId:D}}}", expectedPartitionGuid, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Safety check failed: GPT Partition GUID {{{target.GptPartitionId:D}}} does not match expected installer GUID {expectedPartitionGuid}.");
        }

        DismountAndRemoveVolumeMountPoints(diskNumber, target.StartingOffsetBytes);

        byte[]? mbrDeleteBytes = layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR && target.RawSlotIndex is >= 0 and < 4
            ? Win32StorageInterop.TrySerializeMbrSlotPatchPreservingEbrs(layout, null, target.RawSlotIndex)
            : null;

        if (mbrDeleteBytes is not null)
        {
            CommitRawDiskLayoutBytesNative(diskHandle, mbrDeleteBytes);
            _logger.Info("Cleanup", $"Deleted temporary MBR installer partition (Disk {diskNumber} Partition {installerPartitionNumber}, Slot {target.RawSlotIndex}) via native IOCTL.");
            return;
        }

        var updatedEntries = new List<(RawPartitionLayoutEntry Entry, bool Rewrite)>();
        if (layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT)
        {
            int seq = 1;
            foreach (RawPartitionLayoutEntry p in layout.Partitions)
            {
                if (ReferenceEquals(p, target) || p == target)
                {
                    continue;
                }
                updatedEntries.Add((p with { PartitionNumber = seq++ }, false));
            }
        }
        else
        {
            foreach (RawPartitionLayoutEntry p in layout.Partitions)
            {
                if (ReferenceEquals(p, target) || p == target)
                {
                    updatedEntries.Add((new RawPartitionLayoutEntry(
                        Win32StorageInterop.PARTITION_STYLE_MBR,
                        0, 0, 0,
                        Guid.Empty, Guid.Empty, 0, string.Empty,
                        MBR_PARTITION_UNUSED, false, false, 0, Guid.Empty), true));
                }
                else
                {
                    updatedEntries.Add((p, false));
                }
            }

            while (updatedEntries.Count < 4)
            {
                updatedEntries.Add((new RawPartitionLayoutEntry(
                    Win32StorageInterop.PARTITION_STYLE_MBR,
                    0, 0, 0,
                    Guid.Empty, Guid.Empty, 0, string.Empty,
                    MBR_PARTITION_UNUSED, false, false, 0, Guid.Empty), false));
            }
        }

        CommitDiskLayoutNative(diskHandle, layout, updatedEntries);

        _logger.Info("Cleanup", $"Deleted temporary installer partition (Disk {diskNumber} Partition {installerPartitionNumber}) via native IOCTL.");
    }

    public Task ExtendOsPartitionIntoReclaimedSpaceAsync(
        int diskNumber,
        int osPartitionNumber,
        CancellationToken cancellationToken = default,
        long expectedOsPartitionOffsetBytes = 0)
    {
        return Task.Run(() =>
        {
            string sysDriveRaw = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            string sysDriveLetter = sysDriveRaw.TrimEnd(':', '\\', '/').ToUpperInvariant();
            NativeNtfsShrinkEngine.ExtendOnlinePartitionToMaxAdjacent(
                diskNumber,
                osPartitionNumber,
                sysDriveLetter,
                _logger,
                expectedOsPartitionOffsetBytes);
        }, cancellationToken);
    }

    private static SafeFileHandle OpenPhysicalDriveReadWrite(int diskNumber)
    {
        SafeFileHandle handle = Win32StorageInterop.CreateFileW(
            $@"\\.\PhysicalDrive{diskNumber}",
            Win32StorageInterop.GENERIC_READ | Win32StorageInterop.GENERIC_WRITE,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"Failed to open \\\\.\\PhysicalDrive{diskNumber} (Win32 error {err}). Ensure SWBI is running as Administrator.");
        }

        return handle;
    }

    private static void CommitDiskLayoutNative(
        SafeFileHandle diskHandle,
        RawDiskLayoutSnapshot baseLayout,
        IReadOnlyList<(RawPartitionLayoutEntry Entry, bool Rewrite)> entries)
    {
        byte[] layoutBytes = Win32StorageInterop.SerializeDiskLayout(baseLayout, entries);
        CommitRawDiskLayoutBytesNative(diskHandle, layoutBytes);
    }

    private static void CommitRawDiskLayoutBytesNative(
        SafeFileHandle diskHandle,
        byte[] layoutBytes)
    {
        if (!Win32StorageInterop.DeviceIoControl(
                diskHandle,
                Win32StorageInterop.IOCTL_DISK_SET_DRIVE_LAYOUT_EX,
                layoutBytes,
                layoutBytes.Length,
                null,
                0,
                out _,
                IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"IOCTL_DISK_SET_DRIVE_LAYOUT_EX failed updating partition table (Win32 error {err}).");
        }

        Win32StorageInterop.TryControlCode(diskHandle, Win32StorageInterop.IOCTL_DISK_UPDATE_PROPERTIES);
    }

    private static RawPartitionLayoutEntry? FindOsPartitionInLayout(
        RawDiskLayoutSnapshot layout,
        int osPartitionNumber,
        long knownStartOffsetBytes)
    {
        if (knownStartOffsetBytes > 0)
        {
            RawPartitionLayoutEntry? byOffset = layout.Partitions.FirstOrDefault(p =>
                Math.Abs(p.StartingOffsetBytes - knownStartOffsetBytes) < 65536);
            if (byOffset is not null)
            {
                return byOffset;
            }
        }

        return layout.Partitions.FirstOrDefault(p => p.PartitionNumber == osPartitionNumber);
    }

    private static (long StartOffset, long LengthBytes) LocateAlignedGapForNewPartition(
        RawDiskLayoutSnapshot layout,
        RawPartitionLayoutEntry osPart,
        long diskTotalLengthBytes,
        long requestedSizeBytes,
        int logicalSectorBytes = 512)
    {
        const long oneMib = 1024L * 1024L;
        long alignedRequested = (requestedSizeBytes / oneMib) * oneMib;
        long mbrMaxAddressableBytes = 0xFFFF_FFFFL * Math.Max(512, logicalSectorBytes);

        long usableEnd = layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT &&
                         layout.GptUsableLength > 0
            ? layout.GptStartingUsableOffset + layout.GptUsableLength
            : layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_MBR
                ? Math.Min(diskTotalLengthBytes - oneMib, mbrMaxAddressableBytes - oneMib)
                : diskTotalLengthBytes - oneMib;

        // For MBR primary placement, exclude logical partitions inside an Extended container because the Extended container itself is in sorted!
        List<RawPartitionLayoutEntry> sorted = layout.Partitions
            .Where(p => p.PartitionLengthBytes > 0 &&
                        (layout.PartitionStyle != Win32StorageInterop.PARTITION_STYLE_MBR || p.RawSlotIndex < 4))
            .OrderBy(p => p.StartingOffsetBytes)
            .ToList();

        long afterOsStart = AlignUp(osPart.StartingOffsetBytes + osPart.PartitionLengthBytes, oneMib);
        RawPartitionLayoutEntry? nextAfterOs = sorted.FirstOrDefault(p => p.StartingOffsetBytes >= osPart.StartingOffsetBytes + osPart.PartitionLengthBytes - 4096);
        long afterOsLimit = nextAfterOs is not null ? AlignDown(nextAfterOs.StartingOffsetBytes, oneMib) : AlignDown(usableEnd, oneMib);

        if (afterOsLimit - afterOsStart >= alignedRequested - (64 * oneMib))
        {
            long chosenLen = Math.Min(alignedRequested, afterOsLimit - afterOsStart);
            if (chosenLen >= 512 * oneMib)
            {
                return (afterOsStart, chosenLen);
            }
        }

        long cursor = oneMib;
        foreach (RawPartitionLayoutEntry p in sorted)
        {
            long gapStart = AlignUp(cursor, oneMib);
            long gapEnd = AlignDown(p.StartingOffsetBytes, oneMib);
            if (gapEnd - gapStart >= alignedRequested - (64 * oneMib))
            {
                return (gapStart, Math.Min(alignedRequested, gapEnd - gapStart));
            }

            cursor = Math.Max(cursor, p.StartingOffsetBytes + p.PartitionLengthBytes);
        }

        long tailStart = AlignUp(cursor, oneMib);
        long tailEnd = AlignDown(usableEnd, oneMib);
        if (tailEnd - tailStart >= alignedRequested - (64 * oneMib))
        {
            return (tailStart, Math.Min(alignedRequested, tailEnd - tailStart));
        }

        throw new InvalidOperationException(
            $"Insufficient contiguous unallocated space on disk after shrink (needed {ByteFormatting.FormatGb(alignedRequested)}, largest tail gap {ByteFormatting.FormatGb(Math.Max(0, tailEnd - tailStart))}).");
    }

    private static List<(RawPartitionLayoutEntry Entry, bool Rewrite)> BuildPartitionEntriesWithNewSwbiPartition(
        RawDiskLayoutSnapshot layout,
        long newStartOffset,
        long newLengthBytes,
        Guid newPartGuid,
        ulong gptAttributes,
        out int assignedPartitionNumber)
    {
        uint hiddenSectors = (uint)Math.Min(uint.MaxValue, newStartOffset / 512L);
        var newEntry = new RawPartitionLayoutEntry(
            PartitionStyle: layout.PartitionStyle,
            StartingOffsetBytes: newStartOffset,
            PartitionLengthBytes: newLengthBytes,
            PartitionNumber: 0,
            GptPartitionType: Win32StorageInterop.GptBasicDataTypeGuid,
            GptPartitionId: newPartGuid,
            GptAttributes: gptAttributes,
            GptName: "SWBI - Windows Bootable Setup",
            MbrPartitionType: MBR_PARTITION_FAT32_LBA,
            MbrBootIndicator: false,
            MbrRecognized: true,
            MbrHiddenSectors: hiddenSectors,
            MbrPartitionId: newPartGuid);

        if (layout.PartitionStyle == Win32StorageInterop.PARTITION_STYLE_GPT)
        {
            List<(RawPartitionLayoutEntry Entry, bool Rewrite)> merged = layout.Partitions
                .Where(p => p.PartitionLengthBytes > 0)
                .Select(p => (Entry: p, Rewrite: false))
                .Append((Entry: newEntry, Rewrite: true))
                .OrderBy(x => x.Entry.StartingOffsetBytes)
                .ToList();

            assignedPartitionNumber = 1;
            for (int i = 0; i < merged.Count; i++)
            {
                bool isNew = merged[i].Entry.GptPartitionId == newPartGuid;
                merged[i] = (merged[i].Entry with { PartitionNumber = i + 1 }, merged[i].Rewrite);
                if (isNew)
                {
                    assignedPartitionNumber = i + 1;
                }
            }

            return merged;
        }
        else
        {
            List<(RawPartitionLayoutEntry Entry, bool Rewrite)> mbrSlots = layout.Partitions
                .Where(p => p.PartitionLengthBytes > 0)
                .Select(p => (Entry: p, Rewrite: false))
                .Append((Entry: newEntry, Rewrite: true))
                .OrderBy(x => x.Entry.StartingOffsetBytes)
                .ToList();

            assignedPartitionNumber = 1;
            for (int i = 0; i < mbrSlots.Count; i++)
            {
                bool isNew = mbrSlots[i].Rewrite;
                mbrSlots[i] = (mbrSlots[i].Entry with { PartitionNumber = i + 1 }, mbrSlots[i].Rewrite);
                if (isNew)
                {
                    assignedPartitionNumber = i + 1;
                }
            }

            while (mbrSlots.Count < 4)
            {
                mbrSlots.Add((new RawPartitionLayoutEntry(
                    Win32StorageInterop.PARTITION_STYLE_MBR,
                    0, 0, 0,
                    Guid.Empty, Guid.Empty, 0, string.Empty,
                    MBR_PARTITION_UNUSED, false, false, 0, Guid.Empty), false));
            }

            return mbrSlots;
        }
    }

    private static string WaitForNewPartitionVolumeGuidPath(
        int diskNumber,
        long expectedStartOffsetBytes,
        HashSet<string> volumesBefore,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (RawVolumeBinding vb in Win32StorageInterop.EnumerateAllVolumes())
            {
                if (volumesBefore.Contains(vb.VolumeGuidPath) || vb.DiskNumber != diskNumber ||
                    vb.StartingOffsetBytes != expectedStartOffsetBytes) continue;

                using SafeFileHandle volume = Win32StorageInterop.CreateFileW(vb.VolumeGuidPath.TrimEnd('\\'), 0,
                    Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
                    IntPtr.Zero, Win32StorageInterop.OPEN_EXISTING, 0, IntPtr.Zero);
                if (!volume.IsInvalid && Win32StorageInterop.TryGetSingleVolumeDiskExtent(volume,
                    out int actualDisk, out long actualOffset) && actualDisk == diskNumber && actualOffset == expectedStartOffsetBytes)
                    return vb.VolumeGuidPath;
            }
            Thread.Sleep(250);
        }

        throw new TimeoutException(
            $"Windows Volume Manager did not expose the exact new volume on Disk {diskNumber} at offset {expectedStartOffsetBytes}; an unformatted partition may remain.");
    }

    private static void BringVolumeOnlineIfNeeded(string volumeDevicePathNoSlash)
    {
        using SafeFileHandle vh = Win32StorageInterop.CreateFileW(
            volumeDevicePathNoSlash,
            Win32StorageInterop.GENERIC_READ | Win32StorageInterop.GENERIC_WRITE,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (!vh.IsInvalid)
        {
            Win32StorageInterop.TryControlCode(vh, IOCTL_VOLUME_ONLINE);
        }
    }

    private static string EnsureDriveLetterMounted(string volumeGuidPath)
    {
        string normalizedGuidPath = volumeGuidPath.EndsWith('\\') ? volumeGuidPath : volumeGuidPath + "\\";

        string? CheckExistingMount()
        {
            foreach (string mp in Win32StorageInterop.GetVolumeMountPoints(normalizedGuidPath))
            {
                string trimmed = mp.TrimEnd('\\', '/');
                if (trimmed.Length == 2 && trimmed[1] == ':' && char.IsLetter(trimmed[0]))
                {
                    return char.ToUpperInvariant(trimmed[0]).ToString();
                }
            }
            return null;
        }

        string? existing = CheckExistingMount();
        if (existing is not null)
        {
            return existing;
        }

        HashSet<char> usedLetters = DriveInfo.GetDrives()
            .Where(d => !string.IsNullOrEmpty(d.Name))
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        foreach (char cand in PreferredInstallerDriveLetters)
        {
            if (usedLetters.Contains(cand))
            {
                continue;
            }

            if (Win32StorageInterop.SetVolumeMountPointW($"{cand}:\\", normalizedGuidPath))
            {
                return cand.ToString();
            }

            // Handle async MountMgr automount race where Windows auto-assigned a letter right before SetVolumeMountPointW
            existing = CheckExistingMount();
            if (existing is not null)
            {
                return existing;
            }
        }

        int err = Marshal.GetLastWin32Error();
        throw new Win32Exception(err, $"SetVolumeMountPointW failed assigning a free drive letter to '{normalizedGuidPath}' (Win32 error {err}).");
    }

    private static void DismountAndRemoveVolumeMountPoints(int diskNumber, long partitionStartOffsetBytes)
    {
        foreach (RawVolumeBinding vb in Win32StorageInterop.EnumerateAllVolumes())
        {
            if (vb.DiskNumber != diskNumber ||
                vb.StartingOffsetBytes <= 0 ||
                Math.Abs(vb.StartingOffsetBytes - partitionStartOffsetBytes) >= 65536)
            {
                continue;
            }

            using (SafeFileHandle rw = Win32StorageInterop.CreateFileW(
                vb.VolumeGuidPath.TrimEnd('\\'),
                Win32StorageInterop.GENERIC_READ | Win32StorageInterop.GENERIC_WRITE,
                Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Win32StorageInterop.OPEN_EXISTING,
                0,
                IntPtr.Zero))
            {
                if (!rw.IsInvalid)
                {
                    Win32StorageInterop.TryControlCode(rw, Win32StorageInterop.FSCTL_LOCK_VOLUME);
                    Win32StorageInterop.TryControlCode(rw, Win32StorageInterop.FSCTL_DISMOUNT_VOLUME);
                }
            }

            foreach (string mp in Win32StorageInterop.GetVolumeMountPoints(vb.VolumeGuidPath))
            {
                Win32StorageInterop.DeleteVolumeMountPointW(mp.EndsWith('\\') ? mp : mp + "\\");
            }
        }
    }

    private static long QueryVolumeStartOffsetOrZero(string driveLetter)
    {
        using SafeFileHandle vh = Win32StorageInterop.CreateFileW(
            $@"\\.\{driveLetter}:",
            0,
            Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE | Win32StorageInterop.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32StorageInterop.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (!vh.IsInvalid && Win32StorageInterop.TryGetVolumeFirstDiskExtent(vh, out _, out long off, out _))
        {
            return off;
        }

        return 0;
    }

    private static void ClearGptNoDriveLetterAttributeBestEffort(
        int diskNumber,
        Guid swbiGptGuid,
        long swbiStartOffsetBytes)
    {
        try
        {
            using SafeFileHandle diskHandle = OpenPhysicalDriveReadWrite(diskNumber);
            RawDiskLayoutSnapshot? layout = Win32StorageInterop.TryReadDiskLayout(diskHandle);
            if (layout is null || layout.PartitionStyle != Win32StorageInterop.PARTITION_STYLE_GPT)
            {
                return;
            }

            var updated = new List<(RawPartitionLayoutEntry Entry, bool Rewrite)>(layout.Partitions.Count);
            bool found = false;
            foreach (RawPartitionLayoutEntry p in layout.Partitions)
            {
                bool isTarget = (swbiGptGuid != Guid.Empty && p.GptPartitionId == swbiGptGuid) ||
                                Math.Abs(p.StartingOffsetBytes - swbiStartOffsetBytes) < 65536;
                if (isTarget && (p.GptAttributes & Win32StorageInterop.GPT_BASIC_DATA_ATTRIBUTE_NO_DRIVE_LETTER) != 0)
                {
                    updated.Add((p with { GptAttributes = p.GptAttributes & ~Win32StorageInterop.GPT_BASIC_DATA_ATTRIBUTE_NO_DRIVE_LETTER }, true));
                    found = true;
                }
                else
                {
                    updated.Add((p, false));
                }
            }

            if (found)
            {
                CommitDiskLayoutNative(diskHandle, layout, updated);
            }
        }
        catch
        {
            // Non-fatal: explicit SetVolumeMountPointW still mounts W:\ even if NO_DRIVE_LETTER stays set
        }
    }

    private sealed class MountMgrAutoMountSuppressionScope : IDisposable
    {
        private readonly SafeFileHandle? _mountMgrHandle;
        private readonly bool _restoreEnabledOnDispose;
        private bool _disposed;

        private MountMgrAutoMountSuppressionScope(SafeFileHandle? handle, bool restoreEnabled)
        {
            _mountMgrHandle = handle;
            _restoreEnabledOnDispose = restoreEnabled;
        }

        public static MountMgrAutoMountSuppressionScope TryEnter(ISafeLogger logger)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new MountMgrAutoMountSuppressionScope(null, false);
            }

            try
            {
                SafeFileHandle h = Win32StorageInterop.CreateFileW(
                    @"\\.\MountPointManager",
                    Win32StorageInterop.GENERIC_READ | Win32StorageInterop.GENERIC_WRITE,
                    Win32StorageInterop.FILE_SHARE_READ | Win32StorageInterop.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    Win32StorageInterop.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
                if (h.IsInvalid)
                {
                    h.Dispose();
                    return new MountMgrAutoMountSuppressionScope(null, false);
                }

                byte[] stateOut = new byte[4];
                if (!Win32StorageInterop.DeviceIoControl(
                        h,
                        Win32StorageInterop.IOCTL_MOUNTMGR_QUERY_AUTO_MOUNT,
                        null,
                        0,
                        stateOut,
                        4,
                        out int ret,
                        IntPtr.Zero) || ret < 4)
                {
                    h.Dispose();
                    return new MountMgrAutoMountSuppressionScope(null, false);
                }

                int priorState = BitConverter.ToInt32(stateOut, 0);
                if (priorState == 0)
                {
                    // AutoMount is already disabled on this host; nothing to suppress or restore
                    h.Dispose();
                    return new MountMgrAutoMountSuppressionScope(null, false);
                }

                byte[] disableIn = new byte[4]; // 0 = Disabled
                if (Win32StorageInterop.DeviceIoControl(
                        h,
                        Win32StorageInterop.IOCTL_MOUNTMGR_SET_AUTO_MOUNT,
                        disableIn,
                        4,
                        null,
                        0,
                        out _,
                        IntPtr.Zero))
                {
                    return new MountMgrAutoMountSuppressionScope(h, restoreEnabled: true);
                }

                h.Dispose();
            }
            catch (Exception ex)
            {
                logger.Info("Partition", $"MountPointManager automount suppression skipped ({ex.Message}).");
            }

            return new MountMgrAutoMountSuppressionScope(null, false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_mountMgrHandle is not null)
            {
                try
                {
                    if (_restoreEnabledOnDispose && !_mountMgrHandle.IsInvalid)
                    {
                        byte[] enableIn = BitConverter.GetBytes(1); // 1 = Enabled
                        Win32StorageInterop.DeviceIoControl(
                            _mountMgrHandle,
                            Win32StorageInterop.IOCTL_MOUNTMGR_SET_AUTO_MOUNT,
                            enableIn,
                            4,
                            null,
                            0,
                            out _,
                            IntPtr.Zero);
                    }
                }
                finally
                {
                    _mountMgrHandle.Dispose();
                }
            }
        }
    }

    private static long AlignUp(long value, long alignment) =>
        ((value + alignment - 1) / alignment) * alignment;

    private static long AlignDown(long value, long alignment) =>
        (value / alignment) * alignment;
}
