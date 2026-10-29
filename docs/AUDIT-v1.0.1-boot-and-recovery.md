# SWBI v1.0.1 — Post-Merge Boot & Recovery Audit

> **Note:** file:line references in this document point at the pre-fix tree (`21c96b6`). Several of the
> findings below were fixed afterwards — see `docs/FIXES-safety-hardening.md` for what changed.


**Scope:** static review of the merged v1.0.1 tree (`21c96b6`) focused on interrupted disk operations and
firmware/boot behaviour.
**Method:** source reading with file:line evidence, plus review of the automated tests that are supposed to
cover those paths.
**Not done here:** no build, no test run, no firmware or device access. This sandbox has no .NET SDK
(`dotnet` is absent), so nothing below is a runtime result. Every finding is a code-path claim; each one
says whether it needs Windows hardware to confirm the *consequence*.

Each earlier finding is classified as one of:

- **Confirmed** — the code does what the finding says, provable by reading it.
- **Confirmed, narrower than stated** — a real gap, but the existing code already contains mitigation the
  original wording did not account for.
- **Confirmed, design-level** — a real limitation that cannot be fixed by a code change alone.

---

## 1. Filesystem extension reported as success when it failed — **Confirmed (highest severity of the four)**

**Evidence:** `src/UsbFreeInstaller.Core/Services/Native/NativeNtfsShrinkEngine.cs:503-619`

The function grows the partition, then tries to grow the filesystem, and only the *partition* grow is
checked:

- `IOCTL_DISK_GROW_PARTITION` failure throws (`:566-580`). Good.
- If the volume handle cannot be opened, the entire filesystem step is silently skipped: `if (!volHandle.IsInvalid)`
  (`:588`) has no `else` and no log. A volume without a drive letter, or one that fails to open, produces
  "partition extended, filesystem untouched, success logged".
- The first `FSCTL_EXTEND_VOLUME` result is discarded and only used to trigger a fallback (`:593-615`).
- The fallback `FSCTL_EXTEND_VOLUME` result is discarded too (`:607-615`).
- `:618` then logs `"Extended ... by +N MB via native kernel IOCTLs."` unconditionally.
- There is no re-read of the volume size or of the disk layout after the operation, and the method returns
  `void`, so no caller can distinguish full success from partial success.

**Consequence:** partition larger than the filesystem. The reclaim that post-install cleanup promises does
not happen, the log and UI claim it did, and the user is left with free space that looks allocated but is
unusable. Not a data-loss path — NTFS tolerates the volume being smaller than its partition — but it is a
silent lie about a disk-mutating operation.

**Fix (small, unit-testable):**

1. Fail loudly if the volume handle is invalid, with the drive letter and Win32 error.
2. Capture the return value of both `FSCTL_EXTEND_VOLUME` calls; if both fail, throw `Win32Exception` with
   the last error.
3. Verify the result instead of trusting the IOCTL: re-query `NTFS_VOLUME_DATA_BUFFER` (or the volume's
   `TotalSize` via `GetDiskFreeSpaceEx`) and require the filesystem to be within one cluster of the new
   partition end; log the verified before/after sizes.
4. Return the achieved byte delta (or a small result record) so `WindowsPartitionService:622` and
   `PreparationOrchestrator` can report "extended by X MB" instead of assuming the maximum.

**Test gap:** both live call sites only assert the happy path
(`tests/.../LiveWindowsKernelStorageAndBcdTests.cs:198` and `:344`, each asserting `TotalSize > 1350 MB`).
No test exercises a failing `FSCTL_EXTEND_VOLUME`, which is exactly why this survived the merge.

---

## 2. UEFI cleanup identifies an entry by slot and description — **Confirmed**

**Evidence:** `src/UsbFreeInstaller.Core/Services/Native/UefiBootRegistration.cs:153-176`, `:181-233`, `:235-243`

- Ownership is decided by `IsSwbiLoadOption(entry.Data)` (`:236-242`), which matches only the
  `EFI_LOAD_OPTION` description string against the SWBI/legacy names.
- `Remove` then passes *the entry it just read* as `expectedEntry` to `TryRemoveOwned` (`:165`), so the
  byte comparison that follows only proves "nothing changed since a few milliseconds ago" — it does not
  prove this process created the entry.
- The manifest records no entry fingerprint: `InstallerManifest`
  (`src/UsbFreeInstaller.Core/Models/PreparationModels.cs:40-74`) stores `BcdFirmwareBootGuid` (the slot id)
  and nothing about the load option bytes.

**Consequence / realistic trigger:** slot allocation is "lowest free in `0x0010..0x004F`"
(`UefiBootRegistration.cs:90-103`), so a fresh install lands on `Boot0010` by default. A stale manifest —
for example one from an installation whose entry was already removed — replayed against a later
installation that occupies `Boot0010` with the same description will pass the ownership check and delete the
live entry.

**Sharper consequence I did not see in the original list:** `TryRemoveOwned` deletes `BootNext` whenever it
points at the slot (`:193-203`), with no check that *this* process armed it. So the stale-manifest path can
also cancel a *pending one-time boot* belonging to a different, current installation, not just remove an
entry from `BootOrder`.

**Fix:** put an install-unique nonce into the load option's optional data (the trailing field after the
device path), persist its hash in the manifest (new schema field, e.g. `FirmwareEntryFingerprint`), and make
cleanup require either the exact expected entry bytes or a fingerprint that embeds the nonce. Keep the
description check as an additional guard, not the primary one. The existing rollback path during
`Register` already compares exact bytes (`:122`, `:131`), so only persistent cleanup needs the change.

**Test gap:** `BootSafetyTests.Cleanup_RefusesToDeleteAReusedSlot` (`:157`) uses a *different* description.
There is no test for "same slot, same description, different installation" — the case that actually fails.

---

## 3. Firmware changes are not atomic — **Confirmed, narrower than stated**

Two separate claims here; they are not equally true.

### 3a. Concurrent firmware modification — mostly mitigated, residual window remains

`Register` re-reads and compares `BootOrder`, `BootNext`, and the target slot immediately before *and*
after each write (`:114-139`), and rolls back its own changes on failure (`:142-149`). This is
substantially stronger than a naive read-then-write and the original wording overstates the gap.

What remains true:

- `SetFirmwareEnvironmentVariableExW` has no compare-and-swap, so a token-level writer (firmware setup
  utility, another OS, vendor tool) landing between the `RequireSame` read and the `Write` is overwritten.
  The following verification read detects it and the operation fails — no silent corruption, but the other
  writer's change is lost.
- The same applies to cleanup: `TryRemoveOwned` writes the shortened `BootOrder` and only *then* compares
  (`:214-217`). On a lost race it correctly returns `false` ("cleanup incomplete"), but it has already
  clobbered the concurrent edit.

This is a Win32 API limitation, not a bug in the call sequence. Worth documenting; a real fix needs the
firmware's own variable lock or a vendor UEFI utility.

### 3b. `BootNext` armed before the manifest exists — **Confirmed**, and the consequence is milder than stated

Order in `PreparationOrchestrator.ExecuteLiveAsync`:

- `:235` `ConfigureOneTimeWinPeBootAsync(...)` → writes `Boot00XX`, `BootOrder`, `BootNext`.
- `:245-269` the `InstallerManifest` object is built.
- `:270` `WriteInstallerManifestAsync(...)` persists it.

Crash or power loss between `:235` and the flush inside `WriteInstallerManifestAsync` leaves the firmware
armed with no ownership record on the partition. `WriteInstallerManifestAsync`
(`WindowsPartitionService.cs:483-495`) is a plain `File.WriteAllTextAsync` with no flush-to-disk and no
directory-entry durability, so even a completed call is not crash-durable on the FAT32 installer volume.

The mitigating fact: cleanup fails closed. `CleanupService` requires a matching manifest (GPT GUID, or
MBR disk+size) before deleting anything and otherwise raises a `Blocker` and refuses
(`src/UsbFreeInstaller.Core/Services/CleanupService.cs:163-200`; `:141-160` handles "nothing found"). So the
outcome of this window is a boot entry and partition that SWBI will *not* clean automatically — manual
recovery — rather than a wrong deletion or a corrupt boot configuration.

The in-process failure path is also handled: an exception after registration triggers
`RollbackFailedPreparationAsync`, which removes the firmware entry first and only deletes the partition if
that succeeded (`:453-490`). The unhandled case is specifically process/system death inside the window.

**Fix (ordering, not atomicity):** write a minimal "arm record" before registration — partition GUID,
disk signature/number, sector geometry, and a nonce — then register, then write the full manifest with the
firmware id. Cleanup can then verify the partition from the arm record if the manifest is missing, and
`BootNext` is never armed without an on-disk ownership record. Flush the arm record (`FlushFileBuffers`)
before arming. This also supplies the nonce that fixes finding 2.

---

## 4. Updater checksums don't authenticate the publisher — **Confirmed, design-level**

**Evidence:**

- `GitHubReleaseUpdater.cs:17` states the limitation in a doc comment; `MainViewModel.cs:775` discloses it
  in the update confirmation text.
- `CheckAsync` reads `SHA256SUMS.txt` from the same release (`:50-52`) and compares it against the download
  (`:105-108`). Both are attacker-controlled if the release is compromised.
- The transaction layer (`UpdateFileTransaction.cs:22-31`, `:36-53`) is genuinely careful — re-hash both
  files, `File.Replace` with a retained backup, rollback on failed restart, and a read-only handle held
  across the hash→restart window. It protects against *corruption and local tampering*, not against a
  malicious publisher.
- No Authenticode verification exists anywhere in the tree (no `WinVerifyTrust`, no `X509Chain`, no
  `SignedCms`), and `.github/workflows/release.yml` has no signing step, so the published binaries are
  unsigned and there is currently nothing to verify against.

**Fix requires infrastructure, not just code:** sign releases (Authenticode or a detached signature from a
key the updater pins), then verify the staged file before `File.Replace` — and ideally the running
executable at startup — via `WinVerifyTrust` with a pinned signer thumbprint rather than "any valid chain".
Until a certificate exists, the honest options are (a) keep the current disclosure, or (b) add an opt-in
"require signature" flag that fails closed when no signature policy is configured, which is
indistinguishable from (a) for most users. I would not fake this with a self-check.

---

## What the green CI actually proves

Worth stating plainly, because it bounds all four findings:

- The three live kernel/BCD tests in `LiveWindowsKernelStorageAndBcdTests.cs` **silently return** instead of
  skipping when preconditions are unmet: `:40-42` (needs `SWBI_TEST_ALLOW_HOST_BCD_MUTATION=1`), `:77-80`
  and `:233-235` (need administrator). A returning `[Fact]` is reported as passed by xUnit. So a green run
  can mean "these never executed".
- `.github/workflows/build.yml:41` runs `dotnet test` with no filter and no assertion that the privileged
  tests ran, and does not set `SWBI_TEST_ALLOW_HOST_BCD_MUTATION`, so the host-BCD mutation test is
  guaranteed not to run in CI.
- Nothing in the repository exercises real firmware: UEFI paths are tested against `FakeStore`
  (`BootSafetyTests.cs`), which models the variable API but not firmware policy (some firmware rewrites,
  reorders, or refuses `BootNext`).
- No test covers the failure branch of `FSCTL_EXTEND_VOLUME`, the reused-slot-same-description cleanup case,
  or a crash between arming `BootNext` and writing the manifest.

That is enough to agree with the original conclusion — v1.0.1 cannot guarantee atomic recovery or boot
success — but for concrete, fixable reasons rather than a general lack of confidence.

---

## Topology of remaining risk, prioritised

| # | Finding | Class | Needs hardware to confirm consequence | Fix size |
|---|---------|-------|----------------------------------------|----------|
| 1 | Silent filesystem-extension failure | Confirmed bug | Confirm only on real volumes | Small |
| 2 | Description-only firmware ownership; stale manifest can delete a live entry and cancel `BootNext` | Confirmed bug | NVRAM behaviour only | Small–medium |
| 3b | `BootNext` armed before ownership record is durable | Confirmed ordering gap | Power-loss window only | Small |
| 3a | No CAS on firmware variables; races clobber then detect | API limitation | Yes | Out of scope |
| 4 | No publisher authentication | Design gap | No | Needs signing infrastructure |

## What must be tested on real Windows hardware

Not substitutes for each other; each closes a different question.

1. **Firmware acceptance of `BootNext`** — does a one-time boot actually fire on Dell/HP/Lenovo/ASUS/Supermicro
   UEFI, including when Secure Boot is on, and is the entry consumed (deleted) as expected?
2. **Interrupted-operation recovery** — power-cut between arming and manifest write, and between partition
   grow and filesystem extend; verify cleanup's `Blocker` path and manual recovery steps are accurate.
3. **Stale-manifest replay** — prepare, clean up, prepare again (landing on the same slot), then replay the
   old manifest; confirm nothing live is removed. This one is reproducible in a VM with a synthetic NVRAM
   variable store, but the NVRAM side needs a real machine.
4. **Online extend on real volumes** — BitLocker on/off, Storage Spaces, dynamic disks, 4Kn, and a volume
   with no drive letter (the silent-skip path).
5. **Signed-update path** — only relevant after a certificate exists; until then, verify that a
   tampered-but-same-length download is rejected and that a failed restart restores the backup.

---

## Suggested v1.0.2 content

1. Findings 1, 2, 3b as code changes, each with a unit test on the existing fakes (the `FakeStore` and
   volume-IOCTL seams already exist; the extend fix needs one small seam so the failure path is testable
   without hardware).
2. Replace the silent `return` in the live tests with an explicit skip so CI distinguishes "passed" from
   "did not run", and assert in CI that the unprivileged live tests executed.
3. Keep the existing disclosure wording; it is accurate. Add finding 3a to `docs/ARCHITECTURE.md` as a known
   limitation of `SetFirmwareEnvironmentVariableExW` rather than leaving it implied.
