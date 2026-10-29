# SWBI — Application Breakage & Install-Time User Errors

> **Note:** file:line references in this document point at the pre-fix tree (`21c96b6`). Several of the
> findings below were fixed afterwards — see `docs/FIXES-safety-hardening.md` for what changed.


**Question answered:** what can break the *application itself* (crash, freeze, fail to start, fail to
update), and where does the user hit an error **during** preparation that the tool either fails to prevent,
fails to explain, or fails to surface at all?

**Scope:** the UI/launcher/packaging layer plus the pre-flight checks. The disk/firmware damage paths are in
`docs/AUDIT-failure-and-corruption-modes.md`; the four pre-merge findings are in
`docs/AUDIT-v1.0.1-boot-and-recovery.md`.
**Method:** static reading of the App, Launcher (both implementations), build scripts, workflows, and the
readiness/pre-flight services. No .NET SDK in this sandbox, so nothing here is a runtime result — each item
says whether a run is needed to confirm the *symptom*.

| # | Gap | Severity | Category |
|---|-----|----------|----------|
| 1 | Cancelling leaves the UI unlocked while the disk operation keeps running | **High** | Data loss |
| 2 | Launcher trusts a pre-existing cache folder: marker + EXE is all it checks | **High** (needs a local standard user) | Elevation / startup |
| 3 | ISO inspection runs synchronously on the UI thread — the window freezes | **Medium–High** | UI hang |
| 4 | Failures that never reach the user: `async void` commands, fire-and-forget inspection | **Medium–High** | Missing errors |
| 5 | Two concurrent launches race the payload extraction → "re-download" on a healthy EXE | **Medium** | Startup |
| 6 | Lightweight flavour needs the .NET 8 Desktop Runtime; nothing checks, nothing explains | **Medium** | Startup |
| 7 | Second launcher implementation is dead code that CI still builds; conditional payload → unrunnable EXE | **Medium** | Packaging |
| 8 | No pending-reboot / pending-file-rename pre-flight | **Medium** | Install-time error |
| 9 | No ISO-location durability check — failure lands after C: is already shrunk | **Medium** | Install-time error |
| 10 | `.iso` extension required; mounted ISO or extracted folder rejected | **Medium** | Install-time error |
| 11 | Failure reporting is one line in a status bar; no next-steps panel | **Medium** | Support burden |
| 12 | Reboot uses `bForceAppsClosed` on a 5-second timer | **Low–Medium** | Data loss |
| 13 | Double UAC prompt; elevation args built by string concatenation | Low | UX / robustness |
| 14 | Version string duplicated in five places; only the release tag is validated | Low | Release hazard |
| 15 | UI log ring buffer is 500 lines; in-memory log is unbounded | Low | Diagnostics |

---

## 1. Cancel reports success but the disk keeps being written — **High**

`CancelOperationCommand` does the only thing it can: `_activeOperationCts?.Cancel()`
(`MainViewModel.cs:736`). Whether that stops anything depends on how each service consumed the token, and
the answer is **it does not stop work already in progress**:

- `WindowsPartitionService` wraps every disk operation as `Task.Run(() => NativeCore(...), cancellationToken)`
  (`:191`, `:503`, `:627`). A token passed to `Task.Run` only cancels the *scheduling* of the delegate — once
  `FSCTL_SHRINK_VOLUME`, `IOCTL_DISK_SET_DRIVE_LAYOUT_EX`, or the FAT32 writer has started, it runs to
  completion.
- `RunPreparationAsync` then does exactly the wrong thing with that:

```
catch (OperationCanceledException)
{
    PrepStepTitle = "Cancelled";
    PrepStepDetail = "Preparation was cancelled by user.";
    BusyStatusText = "Operation cancelled.";
}
finally { EndBusyScope(); }        // MainViewModel.cs:1121-1131
```

`EndBusyScope()` sets `IsBusy = false`, which re-enables navigation, "Refresh scan", "Validate ISO", and
`StartPreparationCommand`.

**Consequence:** the user presses Cancel during the shrink or the partition creation, is told "Operation
cancelled", and the wizard unlocks — while the kernel operation is still mutating the partition table in the
background. Pressing Prepare again, or refreshing the scan (which re-reads the layout mid-write), races the
in-flight transaction. This is the mechanism by which a *user-facing* action becomes the corruption path
described in the corruption audit.

**Fix:** cancellation must be *observed*, not assumed. Either (a) make the disk operations non-cancellable
once started and label the button "Cancel after current step" with the scope ending only when the
orchestrator returns, or (b) keep the busy scope open until the operation actually finishes and show
"Cancelling — waiting for the current disk operation to finish…". Minimum viable: do not call
`EndBusyScope()` from a `OperationCanceledException` path unless the orchestrator has returned; the
orchestrator already knows which steps are atomic.

---

## 2. The launcher executes whatever is in its cache folder — **High (local standard user required)**

`ExtractOrResolveEmbeddedPayload` decides the cache is valid like this
(`src/UsbFreeInstaller.Launcher/NativeWin32/swbi_native_launcher.c:643`):

```
if (markerAttrs != INVALID_FILE_ATTRIBUTES && !(markerAttrs & FILE_ATTRIBUTE_DIRECTORY) &&
    exeAttrs    != INVALID_FILE_ATTRIBUTES && !(exeAttrs    & FILE_ATTRIBUTE_DIRECTORY))
    return TRUE;   /* launch it */
```

No ownership check, no ACL check, no hash of any extracted file, no signature. And nothing in the tree sets
an explicit DACL on the runtime folder — `grep` for `SetSecurityInfo`/`DirectorySecurity`/`icacls` returns
nothing (the only DACL reference is a comment about BCD). The resolution order is
`%ProgramData%\SWBI\Runtime` → `%LocalAppData%\SWBI\Runtime` → `C:\SWBI\Runtime` → next to the EXE
(`:363-410`), and the first candidate that passes a *writability probe* wins.

**The chain:** a standard user creates `C:\ProgramData\SWBI\Runtime\app-x64s-<anything>\`, drops in their own
`SWBI-SelfWindowsBootableInstaller.exe` plus a `.swbi-ready` file. The next time an administrator runs the
genuine SWBI standalone EXE, candidate 1 passes the writability probe (it is writable — by the attacker), the
marker+EXE check passes, and the launcher's `CreateProcessW` runs **the planted EXE with the administrator's
elevated token** (`:921`, because the launcher elevated itself first).

Two caveats I want to be explicit about: this needs an existing low-privilege account on the machine, and the
default ACL on `C:\ProgramData` (creator-owner full control on subfolders) is what makes the first step
possible — that should be confirmed on a real machine before treating it as a confirmed vulnerability.
The *cache trust* problem is confirmed regardless: any partial deletion, antivirus quarantine, or botched
update inside `app-*` leaves a folder that the launcher will happily launch, producing "SWBI exited
unexpectedly during startup (code …)" with no self-healing path and no hint that deleting
`C:\ProgramData\SWBI\Runtime` fixes it.

**Fix:** after extraction, (a) harden the folder ACL to Administrators + SYSTEM, (b) record a per-file SHA-256
manifest and verify it before every launch, and (c) validate on mismatch by re-extracting rather than
launching. The payload is already integrity-checked as a *stream*; the same hash should gate the extracted
tree.

---

## 3. ISO validation freezes the UI — **Medium–High**

`ValidateSelectedIsoAsync` (`MainViewModel.cs:901`) is awaited on the WPF synchronization context, and
`WindowsIsoInspector.InspectIsoAsync` only truly yields for the optional SHA-256 pass:

```
sha256Hex = await ComputeFileSha256Async(...).ConfigureAwait(false);   // only if computeSha256
...
using IsoImageStreamReader reader = IsoImageStreamReader.Open(fileInfo.FullName);   // :72
return InspectFromDirectIsoReader(reader, fileInfo, sha256Hex);                    // :73  ← UI thread
```

Everything after that — UDF + ISO-9660/Joliet directory-tree merge, the WIM/ESD XML header parse, edition
enumeration, boot-file resolution — executes **synchronously on the UI thread**. Compare
`WindowsDiskInspector.InspectEnvironmentAsync`, which correctly does `Task.Run(...)`
(`:55`) — the ISO path simply forgot.

**Symptom:** a "Not Responding" window for the duration of the parse, worst with a custom/AIO ISO on a slow
or USB-attached source. The progress bar for the file *copy* stage exists, but during parsing there is no
repaint at all. Unambiguous from the threading; the severity depends on how long the walk takes on your
media, which one run would settle.

**Fix:** `await Task.Run(() => InspectFromDirectIsoReader(...))`, as the disk inspector already does, and
report parse progress through the same `IProgress<int>`.

---

## 4. Errors that never reach the user — **Medium–High**

Three separate mechanisms swallow failures:

**(a) `AsyncRelayCommand.Execute` is `async void` with only a `finally`** (`RelayCommand.cs:48-60`). No
`catch`. Any exception from a command handler lands in `DispatcherUnhandledException`, which shows a generic
modal ("An unexpected UI error occurred") and marks it handled. The command's own state recovers
(`_isExecuting = false`), but the specific failure information and any recovery guidance are gone.

**(b) `RunPreparationAsync` catches only `OperationCanceledException`** (`MainViewModel.cs:1121`). The
orchestrator normally converts failures into a `PreparationResult` with a carefully worded summary — but
anything it throws outside its own `try` (or throws *from* its rollback handler) bypasses that and becomes
(a). The difference matters: the orchestrator's messages contain the recovery instructions
(`OnlineShrinkTransaction.cs:57-77`), the modal does not.

**(c) Fire-and-forget cleanup inspection.** `SelectCleanupTabCommand` does
`_ = CleanupVm.InspectAsync();` (`:712`), `ApplySelectedSimulation` does the same (`:855`), and
`BrowseIsoFile` does `_ = ValidateSelectedIsoAsync();` (`:897`). `CleanupViewModel.InspectAsync` has a
`try/finally` but **no `catch`** (`CleanupViewModel.cs:128-164`). An exception there is collected by
`TaskScheduler.UnobservedTaskException` into `_unhandledErrors` (`App.xaml.cs:84-90`) — which is only ever
read by the smoke test. At runtime the user sees the status text stuck on "Inspecting active boot
configuration…" forever, with no error and no way to retry other than switching tabs.

**Fix:** `catch (Exception ex)` in each async command handler that logs and sets an error state; make
`AsyncRelayCommand` log-and-surface rather than relying on the global handler; and stop discarding the task
from `SelectCleanupTabCommand`/`BrowseIsoFile` (`async void` handler that awaits with a try/catch).

---

## 5. Two quick launches break the first one's extraction — **Medium**

The extraction writes each payload file with `CREATE_ALWAYS` and **`FILE_SHARE_READ` only**
(`swbi_native_launcher.c:746-748`), inside a shared, fixed folder name (`app-<ArchiveId>`). There is no
mutex, no temp-then-rename, and no "extraction in progress" marker.

If a second instance is started while the first is still extracting (double-click, or double-click + Enter),
the second instance sees no marker, starts its own extraction, and gets a sharing violation on the first file
the first instance is holding → `goto cleanup` → `ExtractOrResolveEmbeddedPayload` returns FALSE →
the user gets:

> SWBI could not extract its embedded application runtime. Please re-download the SWBI standalone .exe.

…on a completely healthy EXE, and the only real remedy is re-launching. On a cold first run over a slow disk
or with a real-time AV scanner the window is seconds wide, so it is a plausible first-impression failure.

**Fix:** a named mutex around extraction, extract into `app-<id>.partial` then `MoveFileEx` the directory (or
write the marker last and treat a marker-less folder as stale and re-extract it), and a retry loop with a
short wait when a sharing violation is seen.

---

## 6. The lightweight flavour fails with a misleading message — **Medium**

`build-standalone.ps1 -SelfContained $false` produces `…-win-x64-lightweight.exe`, which is published
framework-dependent. On a machine without the **.NET 8 Desktop Runtime**, the extracted
`SWBI-SelfWindowsBootableInstaller.exe` (an apphost) cannot start; the launcher's 2.5-second monitor catches a
non-zero exit and prints:

> SWBI exited unexpectedly during startup (code 0x80008096). Check logs under: C:\ProgramData\SWBI\Work\logs

The logs do not exist yet (the app never ran), and nothing mentions the missing runtime. That is the single
most common "it won't start" report you will get for the lightweight download.

**Fix:** detect the runtime before launching (probe `Microsoft.WindowsDesktop.App` 8.x under
`%ProgramFiles%\dotnet\shared` or run `hostfxr`), and show "This lightweight build requires the .NET 8
Desktop Runtime — download it from … or use the standalone build." Also state it on the release asset name
or description.

---

## 7. There are two launchers; the unused one is broken by construction — **Medium**

- Shipped: `src/UsbFreeInstaller.Launcher/NativeWin32/swbi_native_launcher.c`, compiled by
  `scripts/Build-SwbiNativeExe.ps1` (clang + MSZIP + `SWBIPK01`/`SWBIEOF1` trailer).
- Also in the solution: `src/UsbFreeInstaller.Launcher/Program.cs`, a C# NativeAOT launcher that embeds
  `SWBI.Payload.zip` — **only** when `-p:SwbiPayloadZip=…` is supplied:
  `<ItemGroup Condition="'$(SwbiPayloadZip)' != '' and Exists('$(SwbiPayloadZip)')">`
  (`UsbFreeInstaller.Launcher.csproj:27`).

`build.yml:37` runs `dotnet build UsbFreeWindowsInstaller.sln`, so the C# launcher **is compiled on
every CI run** without that property. The result is a perfectly green build of an EXE that, when run, throws
`FileNotFoundException("Embedded SWBI application payload ('SWBI.Payload.zip') was not found…")`. Nobody
notices because CI never executes it.

Its staleness shows in other ways too: `string appFolderName = $"app-1.0.0-{hashHex}"` (`Program.cs:116`)
hardcodes `1.0.0` while everything else is 1.0.1, and it uses only the first 6 hex chars of the payload hash
as a cache key.

**Fix:** delete the C# launcher (or exclude it from the solution and the build), so there is exactly one
launcher implementation and CI compiles only what ships.

---

## 8. No pending-reboot pre-flight — **Medium**

`grep` for `PendingFileRenameOperations`, `RebootRequired`, `RebootPending` across `src/` returns **nothing**.
`ReadinessEvaluator` checks 12+ things (`admin-elevation`, `firmware-mode`, `disk-layout`, `efi-system-partition`,
`disk-health`, `existing-installer`, `multi-disk-notice`, `single-disk-topology`, `bitlocker-status`,
`power-ac`, `iso-selected`, `iso-validation`, `iso-arch`, `shrink-space`) but not "Windows is mid-servicing".

**Why it matters:** after a Windows Update that requires a reboot (or with a pending MSI/file-rename
operation), the volume has locked and unmovable files — precisely the case where
`FSCTL_SHRINK_VOLUME` refuses to give back the space. The user gets "Windows reports only N GB can be shrunk"
(`PartitionSizingCalculator.cs:61-63`) which is accurate but reads like a disk-full message, and the real fix
(reboot, then retry) is never suggested.

**Fix:** read `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations`,
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending`, and
`…\WindowsUpdate\Auto Update\RebootRequired`; add a `pending-reboot` check with "restart Windows and re-run
SWBI" as the remediation.

---

## 9. Nothing checks that the ISO will stay readable — **Medium**

The ISO is read live, throughout deployment, from its original path (`IsoImageStreamReader` keeps a handle on
the user-selected file). Pre-flight validates the *content* but never the *location*: an ISO on a network
share, a OneDrive placeholder, a USB stick that is pulled, or a drive that goes to sleep mid-copy produces a
failure **after** C: has been shrunk and the installer partition created — the worst possible moment.

There is also no check that the ISO is not *on the volume being resized*, and no check of available space on
the source volume for the ESD-staging path beyond the sizing model's `RequiredTempOsBytes`
(`PartitionSizingCalculator.cs:34-36`, which is about C:, not the ISO's own volume).

**Fix:** at validation time, record the volume/type/serial the ISO lives on, warn (not block) for
network/removable/cloud-backed paths, and prefer copying the ISO to the SWBI working directory on C: during
deployment when the source is not a fixed local disk. At minimum, fail fast with an explicit message if the
source volume disappears before step 4.

---

## 10. Only `.iso` files are accepted — **Medium**

`WindowsIsoInspector.cs:38-45`: if `Path.GetExtension` is not `.iso`, the result is
`"Selected file must have a .iso extension."` and nothing else happens. Two very common user situations are
unsupported:

1. The user **mounted** the ISO (Explorer's "Mount") and wants to point at the drive.
2. The user has an **extracted** setup folder (from Media Creation Tool's "USB/DVD" option or a 7-Zip
   extraction).

Neither is accepted, and the message reads like a mistake on the user's part rather than a limitation. For a
tool whose pitch is "no USB needed", "I don't have the ISO file, I have it mounted" is a foreseeable first
stumble.

**Fix:** accept a directory input (mounted drive or extracted folder) by skipping the ISO parser and
walking the filesystem for `sources\boot.wim`/`install.*`; or, at minimum, change the message to say that the
`.iso` file itself must be selected and that mounted/extracted media is not supported.

---

## 11. When something fails, the user gets one line — **Medium**

Failure text lands in `BusyStatusText` / `PrepStepDetail`, next to a progress bar. There is no failure panel,
no copy of the failing step, no "what to do now" section, and — because `SetupPartitionGuide` is only
generated on success (`PreparationOrchestrator.cs:281`) — no cheat sheet for the state the machine is
actually in. Meanwhile the Core messages are genuinely good (they name the step, what was already changed, and
what to check). They are simply truncated to a status bar.

For a tool that can leave a machine with a shrunk C:, a temporary firmware entry, and no OS installed, the
failure screen is the most important screen in the product.

**Fix:** on failure, render a dedicated panel: the last successful step, every completed action
(`executedActions` already exists and is populated even on failure), the exact remediation text from the
exception, the log path, and a "Copy diagnostics" button. The data is already assembled — it is only
displayed in the wrong place.

---

## 12. Forced reboot on a short timer — **Low–Medium**

`TryNativeSystemReboot` calls `InitiateSystemShutdownExW(..., bForceAppsClosed: true, bRebootAfterShutdown: true)`
(`NativeUefiAndBcdRegistryEngine.cs:443-448`) with a default 5-second delay (`WindowsBcdBootService.cs:106-109`,
clamped 2–60). The UI confirms before rebooting, but the confirmation dialog does not warn that other
applications will be **force-closed without saving**.

**Fix:** raise the default to 30–60 s, say "save your work — other apps will be closed" in the confirmation,
and consider `bForceAppsClosed: false` first (Windows will still give the standard "these apps are blocking
shutdown" experience).

---

## 13. Elevation: two prompts, string-built arguments — **Low**

The launcher elevates; if that fails it continues unelevated and starts the UI, which then tries `runas`
again from `App.TryRelaunchSelfAsAdmin`. Two UAC prompts back-to-back is a confusing experience, and the
second failure is indistinguishable from the first (both `catch { return false; }`, `App.xaml.cs:235`).

`TryRelaunchSelfAsAdmin` also builds its command line by concatenation:

```
string joinedArgs = string.Join(" ", new[] { "--elevated-relaunch" }.Concat(
    args.Where(...).Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
...
Arguments = joinedArgs,     // App.xaml.cs:219-226
```

Only arguments containing a space are quoted, embedded quotes are not escaped, and `ArgumentList` (which
handles all of this) is not used. Today the arguments are internal flags plus a smoke-report path; a path with
a `"` or a trailing backslash would break the relaunch silently.

**Fix:** pass `--elevated-relaunch` as a marker the launcher consumes (it already drops it, and so does the
app) instead of re-prompting, and use `psi.ArgumentList.Add(...)`.

---

## 14. Version is maintained in five places — **Low**

`SwbiBrand.CurrentVersion = "1.0.1"`, `UsbFreeInstaller.Core.csproj <Version>`, `UsbFreeInstaller.App.csproj
<Version>`, `UsbFreeInstaller.Launcher.csproj <Version>`, and the `-Version` default in
`build-standalone.ps1`. `release.yml:59-62` validates the **tag against `SwbiBrand.CurrentVersion` only** — the
assembly versions are not cross-checked, so a release can ship with VERSIONINFO and the app's self-reported
version disagreeing. The updater compares the tag to the constant, so the visible symptom would be
"SWBI is up to date" while the EXE properties say something else.

**Fix:** generate the constant from MSBuild (`<DefineConstants>` or a `ThisAssembly`-style generated file) so
there is exactly one source of truth.

---

## 15. Log buffers — **Low**

The UI keeps only the last 500 lines (`MainViewModel.cs:1291-1301`) while `SafeRedactingLogger._entries`
grows without bound for the process lifetime (`SafeRedactingLogger.cs:27`). A long deployment is not
per-chunk chatty, so this is not a real memory problem today — but the *file* log is the only complete
record, and there is no rotation or size cap, so repeated sessions accumulate under
`C:\ProgramData\SWBI\Work\logs`.

---

## What the application layer already gets right

- **Startup ordering is sound:** `window.Show()` before `await mainVm.InitializeAsync()`
  (`App.xaml.cs:172-175`), so the user sees the shell immediately rather than a blank/hung window.
- **Broad exception nets exist at the right three levels** — `DispatcherUnhandledException`,
  `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` — and they log before showing
  anything. The problem is only that the *last* of them is invisible to the user.
- **The launcher's reason for existing is legitimate and well handled:** repairing `TEMP`/`TMP`/
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR` before .NET starts, with a `ZLAG~1`-aware probe, is exactly the fix for
  the `0x80008093` single-file failure it documents. CI even poisons `%TEMP%` with `NONEXI~1` to prove it.
- **Elevation is optional-by-design:** `asInvoker` manifest + runtime elevation + a real Dry-Run fallback
  means a broken UAC never produces a dead EXE.
- **`--smoke-test` is a genuinely good harness:** it drives 5 wizard steps × 7 scenarios, both tabs, dry-run
  preparation, cheat-sheet content assertions, and *fails the build on WPF binding errors* via a custom
  `TraceListener`.
- **Redaction is enforced at the logger boundary** (BitLocker 48-digit keys and product keys), so secrets
  cannot leak through a UI message.
- **The portable model is clean:** nothing is installed to Program Files, no services, no registry
  run-keys; uninstalling is deleting the EXE.

## What CI does not cover in this layer

- The smoke test runs `--smoke-test`, which implies `--no-elevate` **and** forces the app to run unelevated,
  so `CanStartPreparation()` is false for live mode: **the live preparation path is never exercised in CI**,
  only the dry-run/simulation path.
- The arm64 EXE is only size-checked, never executed (`build.yml`, step 5). The x86 EXE is executed, but on
  x64 Windows under WOW64 — not on real 32-bit firmware.
- No test launches **two instances at once** (finding 5), no test corrupts the launcher cache (finding 2),
  and no test runs the lightweight EXE on a machine without the Desktop Runtime (finding 6).
- `--cleanup` is exercised once against a machine with no SWBI artifacts, which is the easiest possible
  state for that path.

## Suggested order

1. **§1 cancel semantics** — the only item here that can turn a user action into disk damage; a few lines in
   `RunPreparationAsync` plus honest button text.
2. **§2 cache trust + §5 extraction race** — same file, same fix session; also removes a local escalation
   chain.
3. **§3 UI-thread ISO parse and §4 error surfacing** — both are small, and together they remove the "app
   looked frozen / app crashed with a generic dialog" class of report.
4. **§6 runtime detection, §7 delete the dead launcher** — cheap, removes two confusing failure modes.
5. **§8–§11 pre-flight and the failure panel** — the highest-value *product* work: it converts "it failed"
   into "here is what happened and what to do".
6. §12–§15 as polish.
