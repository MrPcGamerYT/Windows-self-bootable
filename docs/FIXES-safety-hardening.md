# Safety Hardening — What Was Fixed

Fixes applied on top of `21c96b6` (v1.0.1), for the findings in:

- `docs/AUDIT-v1.0.1-boot-and-recovery.md` (pre-merge findings, re-verified)
- `docs/AUDIT-failure-and-corruption-modes.md` (ways the tool can break/corrupt)
- `docs/AUDIT-app-and-install-errors.md` (application + install-time errors)

## Verification status

| Scope | Status |
|---|---|
| C# (App, Core, tests) | **Compiled and tested** on Windows by `build.yml` (run
`40767019025`, `dotnet build -c Release` + Core/WPF unit tests + all four standalone EXEs + GUI smoke test +
8.3 `%TEMP%` torture test + updater replacement test). |
| Native launcher (`swbi_native_launcher.c`) | **Not compiled.** CI publishes through `dotnet publish`; the
zero-CRT launcher is only built by `scripts/Build-SwbiNativeExe.ps1`, which needs Clang/LLD. The change was
reviewed for syntax/scope only (`VerifyCachedExecutable` uses existing helpers `CreateFileW`, `GetFileSizeEx`,
`ReadExact`). **Compile it locally before shipping a native-EXE release.** |
| Anything touching real disks, firmware or `wimgapi` | **Not exercised.** CI runs in a VM without the
firmware/partition scenarios; see the "needs hardware validation" list below. |

The first CI run on this branch failed to compile (one error: the new
`expectedOsPartitionOffsetBytes` parameter was on the implementation but not on `IPartitionService`); that is
fixed in `05a2c3b`, and the follow-up run is green. No runtime behaviour claim in this document goes beyond
what that pipeline covers.

---

## Fixed

### 1. ISO paths can no longer escape the installer partition — **Critical**

- New `SafeRelativePath.TryResolveUnder(root, relativePath, out fullPath, out error)`
  (`src/UsbFreeInstaller.Core/Services/SafeRelativePath.cs`): rejects UNC paths, drive-absolute paths,
  `.`/`..` segments, `:` (drive/ADS), invalid file-name characters and reserved DOS device names, then
  canonicalizes with `Path.GetFullPath` and requires containment in the target root.
- `WindowsIsoDeploymentService` resolves every extraction target through it and throws
  `InvalidDataException` naming the offending ISO entry (no bytes written for that entry).
- `IsoImageStreamReader.ExtractFileAsync` rejects a destination containing a traversal segment (defence in
  depth for any future caller).
- `WindowsIsoInspector` reports such entries as a **validation error**, so a malformed/hostile image is
  rejected before C: is shrunk rather than part-way through deployment.

### 2. Filesystem extension is verified instead of assumed — **High**

`NativeNtfsShrinkEngine.ExtendOnlinePartitionToMaxAdjacent`:

- Optional `expectedOsStartOffsetBytes` / `expectedOsPartitionGuid` identity checks (cleanup now passes the
  inspected offset), so a stale partition number cannot grow a different partition.
- The drive letter's single volume extent must match the target partition *before* the grow.
- Both `FSCTL_EXTEND_VOLUME` results are checked (the second is no longer fire-and-forget).
- The resulting volume size is re-read and compared with the partition size; a partial result throws with the
  exact Win32 error and a `chkdsk`-then-retry instruction instead of logging "Extended … by +N MB".
- The success log now prints the verified before/after volume sizes.

### 3. A completed preparation can no longer be undone by a formatting bug — **High**

- `SetupGuideGenerator` returns a label-based fallback guide instead of throwing when the target disk/partition
  rows cannot be resolved (`First` → `FirstOrDefault` with an explicit fallback path).
- `PreparationOrchestrator` wraps guide generation and the in-place `setup.exe` launch in `try/catch` and
  records a warning action. Previously any exception there reached the outer handler, which **deleted the
  freshly created installer partition and firmware entry** while leaving C: shrunk.

### 4. Installer media durability — **Medium**

- `WriteInstallerManifestAsync` writes `*.new`, flushes to disk, then renames atomically; an interruption can
  no longer leave a truncated manifest (it is the only ownership record for cleanup).
- `NativeWimApi` verifies the rebuilt image starts with `MSWIM` and replaces `boot.wim` via `File.Replace` with
  the previous image kept as `boot.wim.swbi-backup` (falls back to a verified copy where `ReplaceFile` is
  unsupported).

### 5. Application robustness — **Medium**

- `AsyncRelayCommand` catches handler exceptions (and ignores expected cancellations), forwarding them to
  `UnhandledExceptionHandler`, which `App` wires to the logger + smoke-report error queue.
- `CleanupViewModel` has explicit `catch` blocks and a new `StatusDetail` property; a failed inspection no
  longer leaves the tab stuck on "Inspecting…" forever.
- `RunPreparationAsync` handles unexpected exceptions with a specific message, and after a cancelled/failed
  preparation re-scans the disk so the wizard reflects the real state.
- `WindowsIsoInspector` parses the image inside `Task.Run`, so the WPF window no longer freezes during ISO
  validation.
- New machine-wide `SingleInstanceGuard` mutex: a second SWBI instance exits with an explanation instead of
  racing a deploy against a cleanup.

### 6. Launcher cache trust — **Medium**

`swbi_native_launcher.c`: a cache hit now requires a plausible PE image (`MZ` + >4 KB) rather than just an
existing file name; a stale marker is deleted so the payload is re-extracted; if extraction fails because a
second instance is writing the same folder, the other instance's verified result is reused instead of failing
with "please re-download".

### 7. Honest status reporting — **Medium/Low**

- `OptimizeOsVolumeBeforeShrinkAsync` no longer claims a completed optimization pass it does not perform
  (tail evacuation happens inside the shrink transaction); the orchestrator's action text matches.
- `WindowsIsoInspector` flags a pending Windows servicing restart (`pending-reboot` readiness check) with
  "restart, then re-scan" guidance — the most common cause of an unexpected shrink refusal.
- The FAT32 sizing blocker now describes SWBI's own validated cap instead of blaming Windows' formatter.

---

## Tests added

`tests/UsbFreeInstaller.Core.Tests/PathAndGuideSafetyTests.cs`

- Path containment: accepts normal Windows ISO paths; rejects `..`, drive-absolute, UNC, ADS/`:`, and reserved
  device names; rejects paths that resolve to the root itself.
- `ContainsTraversalSegment` behaviour.
- `SetupGuideGenerator` never throws and always yields label-based rules when the target disk is missing or the
  target partition is absent from the snapshot.

---

## Deliberately NOT changed (needs design or hardware, not a one-line fix)

| Finding | Why it is still open |
|---|---|
| `wimgapi.dll` loaded from the user's ISO | Needs a pinned hash or Authenticode check plus a decision on which builds may supply their own DLL. |
| UEFI entry fingerprint / stale-manifest replay | Needs a nonce in the load option plus a manifest schema change; the cleanup contract changes with it. |
| Firmware writes are not atomic (`BootNext` armed before the manifest) | Needs the "arm record first" ordering change; partially mitigated now by the durable manifest write, but the window before the manifest still exists. |
| BitLocker: failed suspension only warns | Product decision (hard-stop vs. continue-in-dry-run) plus FVE state verification; must not be guessed at without a TPM VM. |
| Legacy BCD backup written to `C:\ProgramData` | Needs a second copy on the installer partition + guide text; straightforward but touches the recovery documentation. |
| Updater publisher authentication | Requires signed releases; nothing to verify against until a certificate exists. |
| `.swm` payload hashes / per-part verification | Manifest schema addition. |
| Lightweight build without .NET 8 Desktop Runtime | Detection belongs in the native launcher; needs the runtime-probe design. |
| `MAX_PATH`-length payload entries in the C launcher | Requires long-path (`\\?\`) handling throughout the extractor. |
| Cancel semantics while a kernel IOCTL is in flight | The UI now tells the truth and re-scans, but making cancellation *wait* for the in-flight step needs an orchestrator-level step barrier. |

## Needs real-hardware validation before it can be called fixed

CI proves these compile and that the surrounding logic still passes its unit tests; it does **not** prove the
Windows behaviour. Each line names the specific check a tester should perform on a disposable VM:

1. **Extend identity + verification** — shrink, delete the installer partition, then confirm the OS partition
   grows, that the drive-letter extent check rejects a deliberately wrong partition offset (pass a bogus offset
   in a debug build), and that a refused extend surfaces the Win32 error instead of logging success.
2. **Non-destructive post-success failure** — force `SetupGuideGenerator` to fail (e.g. remove the target row in
   a debug build) after preparation and confirm the installer partition + firmware entry survive.
3. **`boot.wim` replacement** — deploy, then confirm `boot.wim.swbi-backup` exists and the installer still boots;
   repeat on a FAT32 target to exercise the copy fallback.
4. **Pending-reboot check** — with a pending servicing operation, confirm the warning appears and that "restart
   then re-scan" clears it.
5. **Single-instance guard** — launch two copies (including one elevated and one not) and confirm the second
   exits with the explanation under both `Global\` and `Local\` mutex names.
6. **Launcher cache** — truncate the cached EXE / delete the marker and confirm re-extraction; run two launcher
   instances simultaneously and confirm neither reports "re-download".
7. **ISO path rejection** — build a test ISO containing a `..\` entry and confirm it is rejected during
   validation, i.e. before any partition work starts.

