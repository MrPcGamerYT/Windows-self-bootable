# SWBI — How the Tool Can Break or Corrupt Things

> **Note:** file:line references in this document point at the pre-fix tree (`21c96b6`). Several of the
> findings below were fixed afterwards — see `docs/FIXES-safety-hardening.md` for what changed.


**Question answered:** where can SWBI break something, corrupt data, or leave a machine worse than it
found it?
**Tree:** `21c96b6` + the audit commit. Static review only — no .NET SDK in this sandbox, no hardware, no
firmware. Every item names the code that causes it; each says whether the *consequence* needs Windows
hardware to confirm.
**Companion doc:** `docs/AUDIT-v1.0.1-boot-and-recovery.md` (the four pre-merge findings, re-verified).

Severity is about **damage to the user's machine**, not code quality.

| # | Gap | Severity | Needs hardware to confirm | Category |
|---|-----|----------|---------------------------|----------|
| 1 | ISO-controlled file paths extract outside the target partition | **Critical** | No | Host data / security |
| 2 | `wimgapi.dll` is loaded from the user's ISO into the admin process | **High** | No | Host data / security |
| 3 | Legacy BCD backup is stored on the partition Windows Setup will format | **High** | No | Recovery |
| 4 | BitLocker: suspend failure is only a warning; BootOrder edits change PCR7 | **High** | Yes (TPM policy) | Host boot |
| 5 | `ExtendOnlinePartitionToMaxAdjacent` can grow the wrong partition and report success | **High** | Yes | Partition layout |
| 6 | `BootNext` armed before the ownership record is durable | **High** | Power-loss only | Host boot |
| 7 | `boot.wim` is replaced with `File.Copy` on the live file; failures become warnings | **Medium** | No | Installer media |
| 8 | Pre-shrink "optimization" is a no-op reported as completed | **Medium** | No | Honest status |
| 9 | `.swm` payloads written in place, no hash recorded, verification is header-only | **Medium** | No | Installer media |
| 10 | Legacy BCD is mutated in the live hive with no undo | **Medium** | Partly | Host boot |
| 11 | Two instances can run concurrently; no single-instance guard | **Medium** | No | Races |
| 12 | Whole MBR table is rewritten from a parsed snapshot | **Low–Medium** | Yes (EBR/dynamic) | Partition layout |
| 13 | Fixed 32 KB drive-layout buffer truncates silently | Low | No | Partition layout |
| 14 | FAT32 volume has no clean-shutdown flags / backup FSInfo | Low | No | Installer media |

---

## 1. An ISO can write anywhere the admin user can write — **Critical**

**Where:** `WindowsIsoDeploymentService.cs:211-214`, `IsoImageStreamReader.cs:101-136`.

```
string mappedRel = StripDualArchPrefix(entry.RelativePath, archFolderPrefix);
string destFull   = Path.Combine(targetRoot, mappedRel.Replace('\\', Path.DirectorySeparatorChar));
await reader.ExtractFileAsync(entry, destFull, ...);   // FileMode.Create (IsoImageStreamReader.cs:122)
```

`StripDualArchPrefix` → `NormalizeRelPath` only trims leading separators and converts `/` to `\`
(`WindowsIsoDeploymentService.cs:638-647`, `:675-676`). There is **no** `Path.GetFullPath` containment
check, no rejection of `..`, no rejection of `:` (drive-relative), no rejection of reserved device names,
no rejection of alternate data streams. `FileMode.Create` truncates whatever it lands on.

**Trigger:** any ISO whose directory records contain a name of `..` (legal in both ISO9660/Joliet and UDF
directory records; the parser never validates names) or a drive-absolute name like `C:`. SWBI explicitly
supports user-supplied custom/modded ISOs — that is a documented feature, not an edge case.

**Consequence:** an entry named `..\..\..\Windows\System32\ntoskrnl.exe` lands on
`C:\Windows\System32\ntoskrnl.exe` and is overwritten with attacker-chosen bytes, as Administrator, before
the machine is armed to reboot. The host is bricked or owned. `..\..\..\Users\<you>\Documents\...` is
truncated data loss. Escaping to a *different* volume also silently breaks the "everything is on the
installer partition" model that cleanup relies on.

**Fix (small, testable without hardware):** after building the destination, canonicalize both sides and
require containment:
`Path.GetFullPath(destFull).StartsWith(Path.GetFullPath(targetRoot) + Path.DirectorySeparatorChar, OrdinalIgnoreCase)`,
plus reject any path segment that is `.`/`..`, contains `:`, or is a reserved DOS device name (`CON`, `NUL`,
`COM1`…). Reject at *parse* time so the entry list shown to the user is honest. One unit test with three
crafted paths covers it.

---

## 2. The tool loads and executes `wimgapi.dll` from the user's ISO — **High**

**Where:** `NativeWimApi.cs:84` — `NativeLibrary.TryLoad(s_isoHostedWimgapiPath, out IntPtr isoHandle)`,
preferred over the inbox copy at `:94`. The doc comment at `:16` presents this as a feature ("zero DLLs,
self-hosted").

**Trigger:** any ISO containing a `wimgapi.dll`, malicious or merely wrong-architecture-but-loadable.

**Consequence:** arbitrary native code inside an Administrator process that already holds
`\\.\PhysicalDriveN` write handles and `SeSystemEnvironmentPrivilege`. This is a bigger capability than the
tool itself has. It is also how a "custom ISO" becomes a partition-table corruption primitive.

**Fix:** pin the expected SHA-256 of `wimgapi.dll` against the Windows ADK version, or verify its
Authenticode signature (Microsoft) before `TryLoad`; fall back to the inbox `%SystemRoot%\System32` copy when
verification fails. The ISO-hosted path buys nothing that signature-verified loading doesn't also buy.

---

## 3. The legacy-BCD backup lives on the partition Setup will format — **High**

**Where:** `PreparationOrchestrator.cs:233` — `Path.Combine(_appDataDirectory, "BcdBackups")`, and
`_appDataDirectory` resolves under `C:\ProgramData\SWBI` (`SwbiWorkDirectory.cs:126-131`).

**Consequence chain:** SWBI backs up the host BCD to `C:\ProgramData\SWBI\BcdBackups\*.bcd`, then the user
boots the installer and performs a clean install on the OS partition — **which formats C: and deletes the
backup**. If the resulting install fails to boot, the one artifact that could have restored the old boot
configuration is gone. Same for the logs and any staging left under `ProgramData`.

The installer manifest and the payload survive (they are on the installer partition), so the *partition*
half of recovery works; the *boot* half does not.

**Fix:** write the legacy BCD backup to the installer partition (it has a drive letter at that moment) **and**
to `ProgramData`, and record both paths in the manifest. Also add an explicit "copy this file somewhere else
now" step to the setup guide — currently `SetupGuideGenerator` never mentions the backup at all (no
occurrence of "backup" in that file).

---

## 4. BitLocker: nothing stops the run when suspension fails — **High**

**Where:** `WindowsPartitionService.cs:44-61`. `TrySuspendBitLockerInProcess` failing logs a `Warn` and
returns; the orchestrator then records "Suspended BitLocker protection on C: for 1 reboot"
(`PreparationOrchestrator.cs:127-131`) only on the success path, but **proceeds either way**.

**Two distinct ways this bites:**

1. **Protection stays on while the layout changes.** SWBI writes `BootOrder`/`BootNext`
   (`UefiBootRegistration.cs:114-139`). On a TPM-bound BitLocker setup, `PCR7` covers Secure Boot state and
   the boot order, so the *host* OS can demand the 48-digit recovery key on the next ordinary boot, before
   the user has even installed anything. The readiness evaluator does ask the user to confirm the key is
   backed up (`ReadinessEvaluator.cs:345-350`), and that is the only thing standing between this and a
   locked-out machine.
2. **A failed run leaves protection off.** If suspension succeeded and then the shrink fails, protection is
   suspended for one reboot with nothing having been accomplished. The user is less secure with no benefit
   and no prompt to resume.

**Fix:** treat "encryption active and suspension failed" as a hard stop, not a warning; verify suspension by
querying FVE state after the call instead of trusting the return value; and resume protection on the
failure/rollback path (`RollbackFailedPreparationAsync`) when nothing was changed.

---

## 5. The reclaim step can extend the wrong partition and report success — **High**

Two defects that compound.

**5a. Target identity is never checked.** `WindowsPartitionService.cs:622-637`:

```
string sysDriveRaw = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
NativeNtfsShrinkEngine.ExtendOnlinePartitionToMaxAdjacent(diskNumber, osPartitionNumber, sysDriveLetter, _logger);
```

`osPartitionNumber` was captured during *inspection*; the installer partition is deleted between inspection
and this call (`CleanupService.cs:422-437`). The engine then resolves the target by number alone
(`NativeNtfsShrinkEngine.cs:531`): `layout.Partitions.FirstOrDefault(p => p.PartitionNumber == osPartitionNumber)`.
Nothing verifies that this entry is the partition mounted as `SystemDrive`. Neither the start offset nor the
GPT partition GUID is passed or checked, even though `IsSamePartition` already exists for exactly this.

If the number is stale by one — a renumbering, a pre-existing gap ahead of the OS partition, or an OS
partition that is not `SystemDrive` on a multi-boot box — `IOCTL_DISK_GROW_PARTITION` grows
**some other partition** into the reclaimed space, while `FSCTL_EXTEND_VOLUME` is aimed at `C:`.

**5b. The filesystem result is never checked.** `NativeNtfsShrinkEngine.cs:588-618`: the volume-open failure
path has no `else`, both `FSCTL_EXTEND_VOLUME` return values are discarded, and `:618` logs
"Extended … by +N MB" unconditionally. Detail in the companion doc (§1); here the interaction matters: in the
5a scenario the grow targets partition X and the extend targets C:, so C:'s extend fails, and the log still
says success. The user is told "extended C: by 20 GB" while a recovery or OEM partition silently grew and
C: did not.

**Fix:** pass `expectedStartOffsetBytes` + `expectedPartitionGuid` into the extend, resolve the target with
`PartitionLayoutSafety.IsSamePartition`, cross-check the drive letter's single volume extent against that
entry, check both IOCTLs, re-read the final filesystem size, and log the verified before/after. Never claim a
byte delta that was not read back.

---

## 6. `BootNext` is armed before any durable ownership record exists — **High**

Already stated in the companion doc (§3b); listed here because it is a *recovery* break, not just an
ordering nit.

`PreparationOrchestrator.cs:235` arms firmware, `:270` persists the manifest.
`WindowsPartitionService.cs:483-495` writes it with a plain `File.WriteAllTextAsync` — no
`FlushFileBuffers`, no temp+rename, on FAT32.

**Consequence:** power loss in that window leaves a machine that boots into the SWBI entry with no on-disk
record of what it owns. Cleanup then correctly refuses to act (`CleanupService.cs:163-200`), so recovery is
manual: the user must remove the firmware entry themselves or re-run and reconcile. Not corruption, but a
dead end for a non-technical user, arriving exactly when the disk has already been shrunk.

**Fix:** flush a minimal arm-record (partition GUID + offset + nonce) to the installer volume with
`FlushFileBuffers` *before* `Register`, then write the full manifest after.

---

## 7. `boot.wim` is overwritten in place; the whole customization is "non-fatal" — **Medium**

**Where:** `NativeWimApi.cs:680` (rebuild into `tempWorkDir`), `:798-800`
(`File.Copy(rebuiltWimPath, bootWimPath, overwrite: true)`), `:807-810`
(`catch { logger.Warn("Non-fatal warning customizing boot.wim…"); return false; }`).

**Two problems:**

1. `File.Copy` over the live `boot.wim` on the installer partition is not atomic. Interruption leaves a
   truncated boot image — the single payload the firmware entry will boot. Nothing verifies `boot.wim`
   afterwards beyond "exists and non-empty" (`WindowsIsoDeploymentService.cs:138-146`), and a WIM header at
   offset 0 survives a truncation of the tail.
2. Because the whole customization is caught and downgraded to a warning, a *failed* driver injection or
   LabConfig bypass is indistinguishable from success in the outward result. The user is then armed to boot
   a WinPE that cannot see an NVMe controller, or a Windows 11 setup that demands a TPM the machine lacks —
   with the disk already shrunk and no indication why.

**Fix:** rebuild → verify (reopen the WIM, read image count/XML) → `File.Replace`/`MoveFileEx` with
write-through; record `boot.wim` SHA-256 in the manifest; and surface driver-injection failure as a
non-blocking but *visible* warning in the result (or as a blocker when the storage driver was declared
required).

---

## 8. The pre-shrink optimization step does nothing but reports success — **Medium**

**Where:** `WindowsPartitionService.cs:143-153`:

```
public Task OptimizeOsVolumeBeforeShrinkAsync(...)
{
    _logger.Info("DriveTune", $"Using built-in native kernel NTFS tail-cluster evacuator (FSCTL_MOVE_FILE) …");
    return Task.CompletedTask;   // no work performed
}
```

and `PreparationOrchestrator.cs:134-141` then appends
`"Completed {DriveKindDisplay} pre-shrink volume optimization pass on C:."` to the executed-actions list,
which the UI shows as completed work.

**Consequence:** on a fragmented HDD the tail clusters are never moved, so
`FSCTL_SHRINK_VOLUME` cannot reach the requested size and the run fails — after BitLocker was suspended and
after the user was told optimization finished. This is the same "claim without evidence" pattern as the
extend bug, and it makes the shrink success rate worse than the documentation implies.

**Fix:** either implement the compact (`FSCTL_GET_VOLUME_BITMAP` + `FSCTL_MOVE_FILE` are already used
elsewhere in the engine) or remove the step and its user-visible claim. Reporting a no-op as done should not
ship.

---

## 9. Payload parts are written in place, and verification is header-deep — **Medium**

**Where:** `PureCSharpWimSplitter.cs:329-338` (`FileMode.Create` straight onto `install.swm`,
`install2.swm`…).

Good detail worth crediting: the WIM header is written **last** at `:397-399`, so a truncated part has a
zeroed header and *is* detectable. But:

- There is no temp-file-then-rename, so an interrupted run leaves a headerless part in the media tree.
- The manifest records no hashes for the deployed payload, so nothing can distinguish "complete" from
  "complete except the last 4 GB" once the header exists.
- Post-deploy verification checks presence and WIM readability, not length or digest
  (`WindowsIsoDeploymentService.cs:138-169`).

**Consequence:** a run interrupted at the wrong moment produces an installer that boots, starts setup, and
fails partway through applying the image — after the user has formatted their OS partition. The tool would
have warned at step 6 if the header was missing, which makes the window narrow but real (header written
before the final `Flush`.

**Fix:** write each part to `install.swm.partial`, flush, verify length against the computed layout, then
rename; and record per-part SHA-256 + length in `InstallerManifest` so cleanup/setup-side checks are
possible.

---

## 10. Legacy BCD is mutated in the live hive with no undo — **Medium**

**Where:** `NativeUefiAndBcdRegistryEngine.cs:238-261`.

Ordering is defensible: both objects are created fully first, then `DisplayOrder`, then
`BootSequence` (one-time), then `RegFlushKey` — so the *dangerous* pointer is written last, and a mid-way
failure throws `FirmwareBootProvisionException(cleanupIncomplete: true)` rather than pretending to roll back.

What is missing: registry writes have no transaction here. A crash after `SetMultiSzElement(BootSequence)`
but before the flush leaves an armed one-time sequence pointing at an object that may or may not be on disk —
the host boots into a broken entry once. The backup exists but (see §3) it lives on the partition the user is
about to format.

**Fix:** the arm-record from §6 gives you the ability to repair this on the next SWBI start — detect a
`BootSequence`/`BootNext` pointing at an SWBI-described object with no matching manifest, and offer
"remove the stale entry" as a one-click repair.

---

## 11. No single-instance guard — **Medium**

No `Mutex`/named-pipe/single-instance logic exists anywhere in `src` (grep: none). Two SWBI processes can
run at once.

**Consequence:** instance A is mid-deploy on `W:` while instance B runs cleanup, finds the partition by
label, verifies it via the manifest A has already written, and deletes it — or B extends C: into the space
while A is still writing payload. The manifest-based ownership check cannot distinguish "A owns this" from
"B is allowed to delete this".

**Fix:** a global mutex (`Global\SWBI-<diskNumber>` or a whole-tool mutex) plus a lock file with a PID/start
time on the installer volume that cleanup must find unheld before acting.

---

## 12. The MBR path rewrites the whole table from a parsed snapshot — **Low–Medium**

**Where:** `Win32StorageInterop.cs:802-868` (`TrySerializeMbrSlotPatchPreservingEbrs`), `:870-922`
(`SerializeDiskLayout`).

Creating a partition on MBR writes back the entire `DRIVE_LAYOUT_INFORMATION_EX` buffer reconstructed from
the snapshot, with `RewritePartition` cleared on untouched slots and set on the new one. It is careful — it
preserves EBR-derived logical partitions byte-for-byte, keeps the Active flag, and refuses when all four
primary slots are used (`WindowsPartitionService.cs:246-251`).

Residual risks, all needing hardware to settle:

- The snapshot is re-parsed into structs and re-serialized; any field the parser doesn't model is written
  back as zero for *rewritten* entries (GPT name padded/truncated at 35 chars, `:911-912`). For GPT the
  affected entry is only the new one, so this is benign in the create path.
- `RewritePartition` semantics under partmgr for mixed primary/logical layouts is not something a unit test
  can prove. Dynamic disks, Storage Spaces, and Intel RST metadata are not inspected at all.
- The MBR create path gets `MbrHiddenSectors` right (`WindowsPartitionService.cs:336` divides by the
  just-verified logical sector size), but the GPT-only helper `BuildPartitionEntriesWithNewSwbiPartition`
  still hardcodes `512L` at `:777`. Harmless today because GPT ignores that field, and a trap the day the
  helper is reused for MBR on a 4Kn disk.

**Fix:** pass the verified sector size through instead of the literal 512; add an explicit refusal when
dynamic/Storage-Spaces metadata is detected on the target disk.

---

## 13. Fixed 32 KB layout buffer — **Low**

`TryReadDiskLayout` (`Win32StorageInterop.cs:672-683`) allocates 32 KB = header + 227 GPT entries. A disk
with more partitions than that yields `ERROR_MORE_DATA` → `TryReadDiskLayout` returns `null` → the operation
aborts with "unable to read layout". Fails safe, but the failure message will be misleading ("cannot read
partition table" on a perfectly healthy disk). Real disks top out at 128, so this is theoretical unless
someone has a 200-partition GPT.

---

## 14. Formatted FAT32 volume has no clean-shutdown flags / backup FSInfo — **Low**

`NativeFat32Formatter.cs:200-282` writes boot sector, FSInfo, sectors 0–2 + their backups 6–8, both FATs
(cluster 2 correctly marked EOC at `:414-416`), and the root cluster with the label entry. It does not set
the "clean shutdown" bits (`BPB_Resrv`-adjacent flag / FSInfo free-count confidence), and there is no second
FSInfo backup.

**Consequence:** a `chkdsk W:` — which Windows may run automatically, or a user may run out of habit — can
report dirty-bit findings on a freshly formatted volume and "repair" it. The media is functionally correct;
the risk is an unnecessary write on a payload that has no digests (§9).

---

## What the tool already gets right (so the fix list stays honest)

Worth stating, because several of these are the *harder* half and they are done properly:

- **Verify-then-act before any format.** `PartitionLayoutSafety.VerifyBeforeFormatting` cross-checks disk
  identity (GPT GUID / MBR signature), the OS partition, the installer partition's exact offset+length, the
  volume's single extent, and overlap with every other partition, then re-checks NTFS geometry against
  `$Boot` before the first sector is written. Best-in-tree.
- **The partition is verified after the IOCTL, not trusted.** `:374-383` re-reads the layout and requires
  exactly one entry at the exact offset with the expected GUID before proceeding to format.
- **Shrink is a commit/abort transaction** with honest failure text per stage
  (`OnlineShrinkTransaction.cs:19-79`), and the partition is only resized after the filesystem is confirmed
  to fit.
- **UEFI path never touches the host BCD or ESP.** `WindowsBcdBootService.cs:52` and the
  `TryFindExistingSwbiUefiBootSlot` read-only probe. The v0.8.2 changelog's ESP `bootmgfw.efi` replacement is
  gone; current code only *reads* the host's `bootmgfw.efi` as a last-resort payload source
  (`WindowsIsoDeploymentService.cs:482-488`).
- **Firmware registration re-reads and verifies before and after every write**, refuses to overwrite a
  pending `BootNext`, refuses a slot owned by anything else, and never deletes the last boot path.
- **Formatter locks and dismounts the volume before raw writes**, verifies the extent again with the lock
  held, and refuses when the sector size can't be confirmed (`NativeFat32Formatter.cs:68-105`).
- **The update transaction** holds a read handle across hash→restart, retains a verified backup, and rolls
  back on failed restart.

---

## What cannot be settled without a real machine

Ordered by what would change a decision:

1. **Firmware** — does `BootNext` fire and get consumed on Dell/HP/Lenovo/ASUS UEFI with Secure Boot on?
   Nothing in the repo tests real firmware.
2. **BitLocker + BootOrder/PCR7** — does the host demand the recovery key after SWBI writes `BootOrder`?
   Policy-dependent; needs a TPM-bound VM with vTPM plus a physical machine.
3. **MBR with EBRs / dynamic disks / Storage Spaces** — does the whole-table rewrite leave logical drives
   intact and mountable?
4. **4Kn and >2 TB MBR boundary** — `MbrHiddenSectors` and the `0xFFFF_FFFF` sector ceiling.
5. **Online extend on real volumes** — BitLocker on, no drive letter (the silent-skip path), a partition
   number that is not the `SystemDrive` partition (the §5a path in one controlled experiment).
6. **Interruption drills** — power-cut at: after shrink / after partition create / after format / during
   payload write / after `BootNext` arm. Confirm cleanup's report matches reality each time.

## Suggested fix order

1. §1 path containment and §2 DLL verification — cheap, no hardware, removes the only remote-code paths.
2. §3 backup relocation + §4 BitLocker hard stop — removes the two realistic "machine unusable" outcomes.
3. §5 extend identity + result verification, §6 arm-record ordering — removes the false-success and
   unrecoverable-window pair.
4. §7/§9 media durability + manifest digests, §8 remove or implement the no-op step.
5. §10/§11 repair-on-start and single-instance guard.
6. §12–§14 sharpen as hardware results come in.
