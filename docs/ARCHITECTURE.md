# Technical Architecture — SWBI (`Self Windows Bootable Installer`)

## 1. Design Philosophy & Zero-External-Tool Guarantee

**SWBI (`Self Windows Bootable Installer`)** is a modular **C# / .NET 8** Windows desktop application (`WPF` MVVM frontend + pure C# / Win32 `Core` kernel engine + zero-CRT native C self-extracting launcher) engineered to prepare and boot any Official or Custom Windows 10/11 ISO directly from an internal disk without USB media — even on single-drive PCs where only `C:` exists, and even when the running Windows OS is heavily stripped, modded, or partially corrupted.

Production code under `src/` **never** spawns external Windows command-line tools (`diskpart.exe`, `bcdedit.exe`, `dism.exe`, `format.com`, `powershell.exe`, `pnputil.exe`, `reg.exe`, `defrag.exe`, `manage-bde.exe`, or `shutdown.exe`). Every storage, filesystem, WIM/SWM, driver, Registry hive, and firmware operation runs in-process via pure C# and direct Win32 / NT kernel APIs.

```text
UsbFreeWindowsInstaller.sln
├── src/UsbFreeInstaller.Core      (Pure C# + Win32/NT IOCTL domain engine; zero UI dependencies)
├── src/UsbFreeInstaller.App       (.NET 8 WPF MVVM wizard + Post-Install Cleanup UI + swbi.ico)
├── scripts/swbi_native_launcher.c (Zero-CRT Win32 native SFX wrapper immune to broken %TEMP% / ZLAG~1)
├── tests/UsbFreeInstaller.Core.Tests (Unit + live VHDX GPT/MBR kernel + 1:1 bcd.dll binary oracle tests)
└── tests/UsbFreeInstaller.App.Tests  (STA WPF XAML instantiation, binding verification & ViewModel tests)
```

---

## 2. Kernel Storage, Silent Partitioning & FAT32 Engine

### Online NTFS Tail Evacuation & Shrink (`NativeNtfsShrinkEngine`)
1. Confirms the selected NTFS volume has a single extent on the expected disk and partition; checks `$Boot` geometry against the filesystem's reported geometry and the partition length.
2. Calls `FSCTL_SHRINK_VOLUME` prepare, evacuates movable tail clusters via `FSCTL_MOVE_FILE`, then commits the filesystem shrink. It does not delete the NTFS USN journal. A failed commit never proceeds to partition shrink.
3. Flushes and verifies the committed filesystem fits the intended partition, then shrinks the partition with `IOCTL_DISK_GROW_PARTITION` and verifies both again. Before creating or formatting the installer, it rechecks disk, partition, volume extent and NTFS geometry. A later failure may leave an unformatted partition or a smaller filesystem in a larger partition; any length restoration is best effort, not atomic.

### Silent Partition Creation (`MountMgrAutoMountSuppressionScope`, `v0.8.2+`)
- Committing raw unformatted partition sectors while `MountPointManager` AutoMount is active causes Windows Explorer to pop up *"You need to format the disk in drive E: before you can use it."*
- `WindowsPartitionService` opens `\\.\MountPointManager`, queries `IOCTL_MOUNTMGR_QUERY_AUTO_MOUNT` (`0x006D003C`), temporarily disables AutoMount via `IOCTL_MOUNTMGR_SET_AUTO_MOUNT` (`0x006D0040`), and sets `GPT_BASIC_DATA_ATTRIBUTE_NO_DRIVE_LETTER` (`0x8000000000000000`) on GPT entries.
- Rechecks the opened volume's disk extent and length, verifies the disk's logical sector size, and requires a volume lock before formatting `\\?\Volume{guid}` directly in pure C# (`NativeFat32Formatter`: BPB, FSInfo, backup boot sector, dual FAT32 tables, root directory cluster, `SWBI-SETUP` 11-byte volume label). It then restores `MountMgr` AutoMount, clears `NO_DRIVE_LETTER`, and mounts the target drive letter via `SetVolumeMountPointW`.

### Smart GPT & MBR Partition Table Engine (`v0.8.0+`)
- **GPT (`PARTITION_STYLE_GPT`):** Appends a `PARTITION_BASIC_DATA_GUID` (`EBD0A0A2-B9E5-4433-87C0-68B6B72699C7`) partition aligned to 1 MiB boundaries (`<= 128` GPT entries).
- **MBR (`PARTITION_STYLE_MBR`):** Inspects the 4 primary MBR slots (`0..3`), preserves existing `0x80` Active boot flags (both separate `System Reserved` and single-partition Active `C:` topologies), safely skips Extended/Logical (`0x05`/`0x0F` EBR) chains (`RewritePartition = 0`), validates the `2 TiB` LBA32 ceiling, and writes a primary `0x0C` (FAT32 LBA) entry.

---

## 3. Zero-Mount ISO Reader, Pure-C# `.swm` Splitter & Driver Harvester

- **In-Process UDF + ISO-9660/Joliet Reader (`NativeIsoReader`):** Parses optical media images directly from file streams without Windows `Mount-DiskImage` or Virtual CD-ROM drive letters.
- **Streaming `.wim` → `.swm` Splitter (`PureCSharpWimSplitter`):** Splits `> 4 GB` `sources\install.wim` files directly onto FAT32 `SWBI-SETUP:\sources\install.swm`, `install2.swm`, … (`<= 3,800 MB` per chunk) with zero temporary staging space on `C:`. For `> 4 GB` solid-compressed `install.esd` images, `NativeWimApi` self-hosts `wimgapi.dll` extracted from the ISO itself.
- **Standalone Boot File Synthesis:** Ensures `\boot\boot.sdi`, `\efi\boot\bootx64.efi` / `bootaa64.efi` / `bootia32.efi`, and `\bootmgr` exist on `SWBI-SETUP`, extracting fallback copies from `sources\boot.wim` (`\Windows\Boot\...`) when custom/lite ISOs strip them.
- **OEM Driver Harvester (`NativeDriverStoreScanner`):** Exports active third-party `SCSIAdapter`, `HDC`, `System`, and `Net` drivers from `DriverStore\FileRepository` (with fallback reconstruction from `%SystemRoot%\INF` + `System32\drivers`) into `$WinPEDriver$`, `SWBI - Self Windows Bootable Installer Drivers\`, and offline-mounted `sources\boot.wim`.

---

## 4. 1:1 `bcd.dll`-Verified Binary BCD & UEFI NVRAM Engine (`v0.8.2+`)

### Binary `BCD_DEVICE_DATA` Layouts (`NativeUefiAndBcdRegistryEngine`)
Verified byte-for-byte against Microsoft `bcd.dll` / `bcdedit.exe` on physical disks, GPT VHDXs, and MBR VHDXs:

1. **`31000003` (`QualifiedPartition`, `88` bytes total = `16 B` `AdditionalOptions` + `72 B` `PartitionEx`):**
   - `[00..15]`: `AdditionalOptions` GUID (`Guid.Empty` for `{sdiGuid}`)
   - `[16..19]`: `DeviceType = 6` (`QualifiedPartition`), `[20..23]`: `Flags = 0`, `[24..31]`: `Size = 0x48` (`72`)
   - `[32..47]`: **PartitionIdentifier union** — 16-byte `GptPartitionId` (GPT) or 8-byte `StartingOffsetBytes` + 8 zero bytes (MBR)
   - `[48..51]`: **DiskKind** — `0 = GPT`, `1 = MBR` (`[52..55]` reserved `0`)
   - `[56..71]`: **DiskIdentifier union** — 16-byte `GptDiskId` (GPT) or 4-byte `MbrSignature` + 12 zero bytes (MBR)
   - `[72..87]`: 16 zero padding bytes
2. **`11000001` / `21000001` (`BlockIo Ramdisk`, `176` bytes for `\sources\boot.wim`):**
   - `[00..15]`: `{sdiGuid}` (`AdditionalOptions`)
   - `[16..31]`: `DeviceType = 0` (`BCDE_DEVICE_TYPE_BLOCK_IO`), `Flags = 1` (`HasAdditionalOptions`), `Size = 160`
   - `[32..35]`: `BlockIoType = 3` (`Ramdisk`), `[36..55]`: 20 zero bytes (`ImageBase`, `ImageLength`, `ImageOffset`)
   - `[56..67]`: Nested `FileDevice` header (`Type = 1`, `Size = 120`, `Version = 5`)
   - `[68..139]`: 72-byte `QualifiedPartition` struct for `SWBI-SETUP`
   - `[140..175]`: Null-terminated UTF-16LE `\sources\boot.wim\0`

### Firmware-Specific Boot Registration & Cleanup
- **UEFI:** Requires a nonempty installer-local architecture-named EFI loader and standalone BCD (without validating their signatures or bootability); it does not rewrite that BCD or replace the host ESP boot manager. Registers one `BootXXXX` entry, appends it to `BootOrder`, and requests one `BootNext` boot, with readback. It refuses an existing pending `BootNext` and never also arms a BCD boot sequence.
- **Legacy BIOS:** Requires a backup of the active, already-loaded system BCD, then creates a BCD ramdisk entry and one-time boot sequence. It does not replace an existing pending boot sequence.
- **Cleanup limits:** NVRAM and BCD writes are not transactional. The code attempts to remove only entries it can identify as its own; if boot cleanup cannot be verified, it keeps the installer partition. Firmware differences, concurrent changes, power loss and I/O failures can still require manual recovery. Validate on a disposable Windows VM and keep independent recovery media and backups.
