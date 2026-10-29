# Changelog

All notable changes to **SWBI — Self Windows Bootable Installer** are documented in this file.

---

## [Unreleased] — Safety Hardening

- **Critical:** ISO-supplied paths are now resolved and containment-checked before extraction; entries with `..`, drive-absolute, UNC, alternate-data-stream or reserved-device names are rejected during ISO validation (before C: is touched) and again at every write (`SafeRelativePath`).
- **Extension is verified, not assumed:** `ExtendOnlinePartitionToMaxAdjacent` requires the drive letter's single volume extent to match the target partition, checks both `FSCTL_EXTEND_VOLUME` results, re-reads the final volume size, and fails loudly instead of logging success. Cleanup passes the inspected partition offset so a stale partition number can no longer grow a different partition.
- **A completed preparation can no longer be rolled back by a formatting bug:** setup-guide generation and the in-place `setup.exe` launch are now non-fatal, and the guide generator falls back to label-based instructions instead of throwing.
- Installer manifest is written with flush-to-disk + atomic rename; `boot.wim` is verified (MSWIM header) and replaced atomically with the original retained as a backup.
- ISO parsing runs off the UI thread; pending-restart servicing operations are reported in readiness checks.
- UI: async command exceptions are logged and surfaced instead of collapsing into a generic dialog; cleanup inspection/execution failures set an explicit error state; a cancelled preparation re-scans the disk.
- Only one SWBI instance may run at a time (machine-wide mutex), so two windows cannot deploy to and clean the same disk concurrently.
- The launcher now verifies that a cached payload is a plausible PE image before launching it, discards the marker of a stale cache so the payload is re-extracted, and reuses another instance's completed extraction instead of failing with "re-download".
- The pre-shrink "optimization pass" no longer reports completed work it does not perform; the FAT32 sizing message no longer attributes SWBI's own policy cap to Windows' formatter.

---

## [v1.0.1] — Boot, Storage and Update Safety

- Use firmware-only one-time boot on UEFI; do not overwrite the host ESP boot manager or modify the installer-local BCD. Legacy BIOS uses a backed-up system BCD separately.
- Commit and verify NTFS geometry before partition resize, retain the USN journal, and verify identity and volume geometry before formatting. Cleanup is best effort, not atomic.
- Add an opt-in GitHub release updater with architecture/flavor matching, SHA-256 verification, download progress, a retained EXE backup and a verified restart handoff.
- Rename the nonexistent CI short-path fixture to `NONEXI~1` and exercise updater handoff on isolated Windows build copies.
- Automated tests do not guarantee firmware boot or recovery; retain backups and validate on disposable Windows VMs.

---

## [v1.0.0] — Production Release

### Highlights
- **Production-Ready v1.0.0 Milestone:** Complete, field-verified USB-free Windows 10 & Windows 11 self-bootable installer across **UEFI (GPT)** and **Legacy BIOS (MBR)** PCs on **`x64`**, **`ARM64`**, and **`x86` (32-bit)** hardware.
- **Complete Modern Cyber-Slate Dark UI Overhaul:**
  - Redesigned responsive header (`WrapPanel` identity cluster + segmented mode switcher + conditional `Sim Lab` dropdown) eliminating all badge/text clipping on compact `1024×720` / `1366×768` laptop and desktop displays.
  - Replaced all Windows Aero light-theme controls with pure-WPF dark `ControlTemplate`s: custom `8px` dark pill `ScrollBar`s, sliding `PillToggleSwitchStyle` switches, vector-checkmark `ModernCheckBoxStyle` with automatic text wrapping, custom dark `ComboBox` + `Popup` menus, glowing gradient `ProgressBar`s, and dark `TextBox` inputs.
  - Upgraded Step 1 with a 4-tile hardware & capacity dashboard (`Host Windows OS`, `Processor, RAM & TPM`, `System Disk & Table`, `Safe Shrinkable C:`), a continuous proportional disk ribbon bar above glass partition cards, and severity-tinted readiness cards.
  - Added a connected 5-step wizard bar with emerald checkmark completion badges and a sleek collapsed-by-default bottom **Live Kernel, Storage & Boot Console** bar showing live event count and latest log preview, freeing ~190px of vertical workspace on startup.
- **Complete Documentation & User Guide:** Added comprehensive step-by-step user guide (Preparation, Inside Windows Setup partition rules, and Post-Install Cleanup), special scenario guides (Win10 → Win11, Win11 → Win10 downgrade, 32-bit → 64-bit cross-grade, Custom/Lite ISOs), under-the-hood architecture reference (`docs/ARCHITECTURE.md`), `LICENSE` (MIT), `SECURITY.md`, and `CHANGELOG.md`.
- **Step 4 In-Place Live Mode Toggle & Full PE Metadata:** Added a direct `Dry-Run Safety Mode` toggle inside Step 4's safety card alongside complete Win32 `VERSIONINFO` and .NET assembly metadata across all four standalone `.exe` executables.

---

## [v0.8.2] — Bulletproof One-Time Restart Boot & Silent Partition Formatting
- **Eliminated Explorer Raw-Partition Format Popup:** Added `MountMgrAutoMountSuppressionScope` (`IOCTL_MOUNTMGR_SET_AUTO_MOUNT`) and `GPT_BASIC_DATA_ATTRIBUTE_NO_DRIVE_LETTER` so Windows Explorer never displays *"You need to format the disk in drive E: before you can use it"* during partition creation.
- **1:1 `bcd.dll`-Verified Binary BCD Provisioning:** Verified `31000003` (`QualifiedPartition`, `88` bytes) and `11000001` (`BlockIo Ramdisk`, `176` bytes) byte-for-byte against Windows `bcdedit.exe` on physical disks, GPT VHDXs, and MBR VHDXs.
- **Automatic ESP `bootmgfw.efi` Upgrade Sync:** Automatically backs up and updates an older Windows 10 ESP `bootmgfw.efi` when preparing a newer Windows 11 ISO so `winload.efi` boots cleanly without version-mismatch errors.
- **Reliable Hive Flush & `SYSTEM`-DACL Cleanup:** Fixed `RegUnLoadKeyW` binding for standalone BCD/WIM hives and implemented recursive `REG_OPTION_BACKUP_RESTORE` + `NtDeleteKey` BCD cleanup.

---

## [v0.8.1] — 100% Standalone `.exe` Releases Across All Architectures
- **Zero `.zip` Archives:** Switched every release target (`win-x64-standalone`, `win-arm64-standalone`, `win-x86-32bit`, and `win-x64-lightweight`) to direct standalone `.exe` executables compiled via `swbi_native_launcher.c` + Clang/LLD (`0x8664`, `0xAA64`, `0x014C`).

---

## [v0.8.0] — Smart MBR & GPT Partition Table Engine
- **Full Legacy BIOS + MBR & UEFI + GPT Support:** Added raw MBR slot inspection, single-partition Active (`0x80`) `C:` preservation, Extended/Logical (`0x05`/`0x0F`) EBR safety, `2 TiB` LBA32 boundary validation, firmware-vs-partition mismatch detection, and automatic Win11-on-MBR `LabConfig` provisioning.
- **Fixed Online NTFS Shrink (`Win32 error 87`):** Aligned `SHRINK_VOLUME_INFORMATION` to its exact 24-byte kernel layout and reserved 1 cluster for the NTFS backup boot sector.

---

## [v0.7.0] — Universal `x64`, `ARM64`, `x86` (32-Bit) & `32-Bit → 64-Bit` Support
- Added native `win-x86` and `win-arm64` support plus hardware `CPUID` Long Mode detection enabling direct USB-free **32-bit Windows 10 → 64-bit Windows 10/11** clean upgrades.

---

## [v0.6.0] — 100% Zero-Windows-Tool Self-Contained Kernel Engine
- Replaced all external CLI calls (`diskpart`, `bcdedit`, `dism`, `format.com`, `powershell`, `shutdown`) with in-process C# & Win32/NT kernel engines (`NativeNtfsShrinkEngine`, `NativeFat32Formatter`, `PureCSharpWimSplitter`, `NativeIsoReader`, `NativeUefiAndBcdRegistryEngine`, `NativeDriverStoreScanner`) and `%TEMP%` / `ZLAG~1`-immune execution (`C:\ProgramData\SWBI`).

---

## [v0.3.0 – v0.5.0] — Initial SWBI Wizard, Custom Branding & Universal ISO Support
- Introduced **SWBI — Self Windows Bootable Installer** WPF wizard, custom `swbi.ico` branding across UEFI/BCD/Setup/Explorer choosers, Official + Custom/Modded ISO support, OEM storage/network driver harvesting, and Post-Install Cleanup utility.
