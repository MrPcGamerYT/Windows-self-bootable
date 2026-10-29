# Security & Data-Safety Policy

## 1. Zero-Telemetry & 100% Offline Operation

**SWBI (`Self Windows Bootable Installer`)** operates **100% locally and offline** on your machine:
- **Zero network connections:** SWBI makes no outbound HTTP/DNS requests, collects zero analytics or telemetry, and never uploads hardware profiles, logs, or product keys anywhere.
- **Redacted local logs:** Diagnostic session logs redact usernames and sensitive paths automatically.

---

## 2. Verifying Official `.exe` Downloads (`SHA256SUMS.txt`)

Every official GitHub Release includes a `SHA256SUMS.txt` file generated automatically inside GitHub Actions (`windows-latest`) directly from the source tree.

Before running a downloaded `.exe`, you can verify its cryptographic SHA-256 checksum in PowerShell:

```powershell
Get-FileHash .\SWBI-SelfWindowsBootableInstaller-v1.0.0-win-x64-standalone.exe -Algorithm SHA256
```

Compare the printed hash with the matching line in `SHA256SUMS.txt` on the [GitHub Releases](https://github.com/MrPcGamerYT/Windows-self-bootable/releases) page.

---

## 3. Built-In Disk & Boot Safety Controls

- **Dry-Run Mode Default:** Starts in safe simulation mode (`0` disk writes) until explicitly switched to Live Mode with mandatory backup and partition confirmations.
- **8 GB OS Reserve Floor:** Never shrinks `C:` below an 8 GB free-space safety buffer.
- **Pre-Boot Integrity Gate:** Verifies `boot.wim`, `boot.sdi`, EFI/BIOS bootloaders, `setup.exe`, and `.wim`/`.swm`/`.esd` headers *before* modifying `BCD` or UEFI NVRAM.
- **Automatic Atomic Rollback:** Exports a binary backup of `HKLM\BCD00000000` (and backs up host `bootmgfw.efi` when updated) prior to boot provisioning, automatically rolling back partial state on error or cancellation.
- **Strict Cleanup Ownership Checks:** Post-install cleanup validates `PartitionGuid`, volume label (`SWBI-SETUP`), `.swbi-installer-manifest.json`, independent OS boot state, and physical partition adjacency before deleting or extending anything.

---

## 4. Supported Versions & Reporting Issues

| Version | Supported |
|---|---|
| **`v1.0.x`** | **Yes (Active Production Release)** |
| `< v1.0.0` | Upgrade to `v1.0.0+` |

To report a security or data-safety issue, please open a detailed issue or security advisory on [GitHub (`MrPcGamerYT/Windows-self-bootable`)](https://github.com/MrPcGamerYT/Windows-self-bootable/issues) including your Windows build, disk partition style (`GPT` or `MBR`), and the redacted SWBI session log.
