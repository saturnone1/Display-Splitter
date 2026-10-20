# Building DesktopSplitter

## Prerequisites

All eight native projects (driver, compositor, installer, helpers and probes)
use **C++20** (`LanguageStandard=stdcpp20`) in every declared configuration,
including the driver's x64/ARM64 Debug/Release configurations. The compositor
and helpers retain the v143 toolset; the driver retains its WDK toolset.
The language upgrade does not change the Windows 10 21H2 baseline.

| Component | Needs | Status on a stock dev box |
|-----------|-------|---------------------------|
| `compositor/` | VS2022 (C++ v143), Windows SDK | builds out of the box |
| `installer/devcreate/` | VS2022 (C++ v143) | builds out of the box |
| `app/` | .NET 10 SDK + WPF UI (NuGet restore) | VS2026 or `dotnet` CLI; SDK selected by `global.json` |
| `driver/` | **WDK 10.0.26100** + VS2022 WDK extension | must be installed manually |

The app and diagnostic tools target `net10.0-windows`. `global.json` selects
a stable .NET 10 SDK (10.0.100 or a newer 10.0 feature band); Visual Studio
2026 is required for .NET 10 IDE support. C++/WDK tooling is independent of
the managed target framework. The repository's `NuGet.Config` uses nuget.org
without inheriting machine-specific package feeds.

The UI uses WPF UI 4.3.0, standard native window chrome, and opaque surfaces
on both Windows 10 21H2 and Windows 11. See [COMPATIBILITY.md](COMPATIBILITY.md).

For C++ compilation on a Korean Windows installation, use UTF-8 source decoding
in the build shell before running `build.ps1`:

```powershell
$env:CL = "$env:CL /utf-8"
.\build.ps1 -NoVersionBump
```

### UI checks without changing the desktop

From the repository root:

```powershell
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release -- artifacts/ui-smoke
```

This loads the compiled safety dialogs, renders light/dark/high-contrast
previews, and checks their Keep/Revert/Cancel event wiring. The main layout
is rendered from the actual XAML with inert sample data and one-way bindings.
It never shows a window, starts the real view model, installs a driver, or
changes a display. PNGs are written under `artifacts/ui-smoke`.

Headless lifecycle regression checks use the actual main window and preview
view model, create a hidden native handle, and never show a window or mutate
display settings. Each command runs in its own process:

```powershell
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release -- --lifecycle-close
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release --no-build -- --lifecycle-exit
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release --no-build -- --lifecycle-preview
```

They cover cancelling a close, subsequently closing, application exit and
preview shutdown. Shutdown starts after `Closed`; it must not call `Close()`
again from `Closing`.

For interactive UI review without display mutations, launch the built app
with `--ui-preview`. Layout selection and size editing work; Apply, Revert,
display hiding, startup registration and preference writes are blocked, and
startup recovery/automatic apply and exit-time display restoration are skipped.
Closing exits without a tray icon. Ordinary launches retain the full product
behavior, including recovery and saved automatic-apply settings.

Use `--manual-review` for interactive review with the normal buttons enabled.
This mode skips startup auto-apply, stale-display recovery, automatic startup
registration refresh, and exit-time display restoration. Explicit user actions
retain their usual behavior, validation and confirmation dialogs. It uses the
normal tray behavior. To check this mode's controls and shutdown headlessly:

```powershell
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release -- --lifecycle-manual
```

To exercise read-only hiding state failures/cancellation and driver preflight:

```powershell
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release -- --state-checks
```

The Korean main layout is rendered off screen at regular and compact sizes
with light, dark and high-contrast themes by the standard UI checks above.

The experimental details panel also has a compiled-view regression check. It
expands the actual panel with real read-only status rows, without rewriting
bindings or invoking any display command. Error reporting is tested separately
with nested failures and duplicate notifications, without message boxes:

```powershell
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release -- --hiding-ui
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release --no-build -- --error-reporter
```

All UI test hosts construct `App(suppressStartup: true)`: WPF queues startup
from the Application constructor, so even an offscreen dispatcher pump must
explicitly suppress product startup, automatic apply/recovery and window creation.
The compiled panel check also asserts that product startup did not construct its
view model, saved settings are unchanged and (when installed) the driver's active
monitor count is unchanged. Real monitor hiding is not exercised by these tests.

### Stability and safety checks

Safety confirmation remains enabled: an unconfirmed change reverts after the
configured `SafetyConfirmationSeconds` (default 15). The collapsed **안전 복원 및
시작 설정** panel lets the user adjust it. The deadline starts before dispatching
the dialog, independently of the UI; displayed seconds use a monotonic clock.
Missing or failed confirmation UI declines the change. Closing either a safety
dialog or manual Settings guide completes its waiting caller.

Compositor stop requests wait for actual process exit without forced termination.
Monitor arrival/removal and specialization completion use Windows notifications
and state reads, with explicit cancellation instead of guessed completion after
fixed delays. Cancellation never records successful removal. A partially mutated
Apply compensates once using the previous split, independently of cancellation;
failed compensation reports both errors without recursive retries. Automatic
cleanup preserves the user's startup preference. Hiding ownership is journaled
before mutation and retained if restoration cannot be verified.
The journal includes the monitor interface path: recovery resolves its current
adapter/target through all connected CCD paths, including inactive targets,
rather than treating a hidden output as disconnected or guessing a GDI number.

Settings updates are serialized per file across processes and committed through
a flushed temporary file plus atomic replacement. A failed commit retains the
old file; corrupt input is not silently overwritten by an update. Virtual monitor
ordering requires unique driver EDID indices, and hiding hashes come only from
observations for that exact device. Periodic read-only reconciliation, IPC
responsiveness bounds, and safety deadlines remain intentional.

The following tests use synthetic state, temporary files and a fake clock; they
never operate real displays, move windows, register hotkeys or stop processes:

```powershell
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release -- --stability
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release --no-build -- --confirmation-ui
```

The second check deliberately leaves the WPF dispatcher unpumped until the fake
safety deadline expires, then verifies that no late dialog appears. These checks
do not establish Windows 10 21H2 or Windows 11 hardware compatibility.

### Refactoring and performance diagnostics (2026-10-20)

The user's saved hiding preference now overrides the legacy HKLM default even
when the app cannot write that machine-wide mirror. `ApplyService` is separated
into orchestration, hiding, primary-display handling and contract files.

User actions (including confirmation and compensation) hold one cancellable,
reentrant operation lease. Actual display mutations use a separate lease. UI
guides, hiding confirmation and the elevated helper release only the mutation
lease, allowing the secure-desktop watcher to reconcile primary ownership while
other user operations remain serialized. Watcher shutdown cancels a contended
lease before joining its worker. Device identity is checked independently of GDI
display numbering, and helper target IDs are resolved again before launch.

Startup reads desktop state on a worker before enabling Apply. Startup recovery
finishes before auto-apply can start; a read-only hiding probe does not block
editor readiness. Refreshes share one reader, discard obsolete results/errors
and preserve edited dimensions. Closing cancels the wait and prevents late
results from being published, without terminating the underlying native call.

The visible log retains up to 64 Ki characters and publishes once per dispatcher
batch. Session diagnostics are queued to `%LOCALAPPDATA%/DesktopSplitter/logs`,
with bounded queue, file size and retention. Queue overflow and oversized records
are explicitly marked. Normal exit drains the writer; no periodic flush timer is
used. Existing UI-error logs are separate from session-log retention.

```powershell
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release -- --refactor
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release --no-build -- --performance
```

The refactor checks use fake desktop snapshots and gates; they also cover a
blocked read completing after view closure, stale failures, edit preservation,
burst-log UI publications, file rotation and retention. They do not mutate real
displays, register shortcuts or launch the compositor. The synthetic benchmark
compares 1,024 log entries of 160 characters with bounded history published once
per 32 entries. It writes `artifacts/refactor-performance.json`. One local run
allocated 170,057,464 bytes for the previous concatenation path versus 3,437,176
bytes for the bounded/batched path; timing is environment-sensitive. This is
neither an application startup benchmark nor a display/FPS measurement.

Compositor `--verbose` reports acquisition/idle poll counts, copy-call duration,
CPU staging payload throughput, and p95/p99 CPU `RenderFrame` call duration over
the latest 256 samples. These are CPU timings, not GPU execution timestamps or
end-to-end input latency. No textures or arrays are allocated per sample. The
existing zero-timeout acquisition and pacing behavior are preserved until real
hardware measurements justify a capture-path change. Frame-statistics math is
covered by GeometryProbe. Actual Windows 10 21H2/11, UAC, sleep/resume and
same/cross-adapter rendering verification remain hardware validation work.

### Split matrix representatives (2026-10-20)

Live shortcut synchronization uses an explicit WPF DispatcherSynchronizationContext
at native hotkey/tray/public async entry points; same-thread CheckAccess alone is
insufficient for async continuations from a native hook. Monitor collection refresh
suppresses transient two-way SelectedItem changes throughout incremental updates.
The previous clear/repopulate path let a temporary null monitor rebuild valid sizes
as 1920x1080 defaults, then reuse invalid 960x540 grid sizes and keep Apply disabled.
The current-state list reads verified compositor routing and selected-slot settings
after shortcut operations, separately from the next-apply editor.
`ui-smoke --live-sync` drives the actual shortcut entry without a WPF context,
using fake desktop actions. It checks WPF notification context, actual bound button
states after success/failure/cancellation, preserved sizes and monitor identity,
live swaps/selection, merge and subsequent split. It never registers keys, shows
overlay windows, changes displays or moves real app windows.

Further stability/refresh cleanup uses value snapshots for monitor descriptions
and updates only changed collection entries. Same-monitor refreshes retain editor
objects and partially typed text instead of round-tripping input through integers.
The live-sync fixture verifies zero monitor/editor/live-row collection mutations
across ten equal snapshots with freshly allocated descriptions. This measures UI
refresh churn, not compositor frame rate. All four display-operation completion
paths share one unlock-and-refresh routine; one final read covers in-operation
display notifications. Verified compositor routing is reused instead of queried
again immediately after verification.

A known physical monitor path cannot fall back to a recycled DISPLAYn or ambiguous
friendly name. Disconnection leaves Apply unavailable for that missing selection,
preserves its draft, and remembers the identity for reconnection. A dropdown slot
change is pending until explicit selection succeeds; it cannot write group settings
through unrelated preference saves or change the live badge before validation.

The UI now calls this **선택 칸** (selected slot). Selection stays with a fixed
layout position when desktops are swapped; it does not follow a virtual device.
Merge captures the current verified compositor routing. If capture fails, the
matching older window memory is discarded instead of reused for a later split.
Unknown routing is rejected for matrix operations. Tests cover all 24 four-slot
permutations through merge and re-split with new device names.
After merge, the UI displays one original screen and collapses the draft editor.
Saved split settings remain available under the explicitly labeled next-apply
editor; selecting a layout opens it without changing the current desktop.

The matrix panel selects a **representative slot inside the selected physical
monitor's split group**. It does not select Windows' global primary monitor.
Ctrl+Alt+Shift+F5/F6/F7/F8 select representative slots 1/2/3/4. The former foreground-window
move actions are retired (their serialized enum IDs stay reserved). Existing
custom arrangement/app keys are preserved; conflicting new defaults are disabled
per action instead of taking a custom key. Other arrangement/app defaults remain
Ctrl+Alt+Shift+F1..F4 and F9..F11.

Representative choices and remembered window matrices are keyed by the physical
monitor interface path and layout. An explicit merge records at most one eligible
front window per slot, restores those representatives onto that same physical
monitor and requests bringing the chosen slot's window to the front. An explicit
split restores the remembered representatives to their corresponding slots after
display confirmation. There are no app launches or TOPMOST flags. Closed,
minimized, inaccessible or reused windows are skipped; there is no fallback to a
different monitor's foreground/primary window. Preparation occurs before topology
changes so a representative manually moved to another physical monitor is left
there. Group/destination mismatches reject the restore before moving any window.

Window identity includes HWND, process start time, class and a private per-profile
window-property token. Retired profile properties are removed when possible;
integer values hold no allocated resources. At most eight matrix profiles are
retained. An earlier-build split with no recorded physical identity is not guessed;
matrix capture starts after applying a split from this build. Automatic startup,
crash cleanup and exit restoration do not capture or restore matrix windows.

Selecting the matrix representative only writes group preferences. Separately,
the split pipeline preserves the original Windows primary when it belongs to a
different physical monitor. Splitting the Windows-primary physical monitor still
hands that role to the first virtual target as required by the existing presenter
workflow, independently of the chosen matrix representative.

Window placement follows [WINDOWPLACEMENT workspace coordinates](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-windowplacement)
and uses its asynchronous flag so a different app's window thread cannot block
the caller. Window-property access respects [SetProp UIPI restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setpropw).
No foreground-lock workarounds are used. Requests may be refused by Windows or
individual apps; actual maximized-window/focus behavior remains a hardware/UI test.

```powershell
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release -- --matrix
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release --no-build -- --matrix-ui
```

The matrix engine test uses a fake window adapter and covers group isolation,
foreign Windows-primary preservation, capture ordering, merge/re-split targets,
manual moves to another monitor, closed/recycled handles, cancellation and JSON
round-trips. The compiled UI test uses a synthetic four-slot desktop and offscreen
rendering, asserts that preview guards work and leaves product settings unchanged.
Neither test calls the live matrix adapter or actually moves/reorders any window.

To produce the control app with its .NET runtime included:

```powershell
dotnet publish app\DesktopSplitter.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -o artifacts\app-win-x64
```

This is the app payload, not the complete product installer: splitting still
requires `dscomp.exe` and an installed, signed virtual display driver. The
existing setup staging script also publishes the app self-contained.

### Native compatibility checks without acquiring displays

Build `compositor/tests/GeometryProbe.vcxproj` and
`compositor/tests/PresenterProbe.vcxproj` as Release/x64, then run their binaries
under `compositor/build/x64/Release`. These probes use geometry values and COM
test doubles, not a real display manager. The presenter probe tests both the
Windows 10 legacy task interface and the newer result-bearing interface.

After building the native Release payloads, run:

```powershell
.\tools\check-native-dependencies.ps1
```

This reads executable imports with `dumpbin`; it never runs the payloads.
Release `dscomp.exe` and `edidoverride.exe` statically link the C++ runtime
because the installer does not deploy VC++ redistributables. Rebuild native
payloads with a serviced compiler when updating their bundled runtime.

### Installing the WDK (required for the driver only)

The WDK version must match an installed Windows SDK (this repo assumes
10.0.26100):

```powershell
winget install Microsoft.WindowsWDK.10.0.26100
```

Then install the **WDK Visual Studio extension** (offered at the end of WDK
setup, or run the VSIX from
`C:\Program Files (x86)\Windows Kits\10\Vsix\VS2022\10.0.26100.x\WDK.vsix`).

## Versioning

The root **`VERSION`** file is the single source of truth for the product
version (`MAJOR.MINOR.PATCH`).

| Component | Who changes it | How |
| --- | --- | --- |
| **PATCH** | `build.ps1`, automatically | Incremented on every **successful** build |
| **MINOR** | **you, by hand** | New functionality / notable change: `.\build.ps1 -SetVersion 0.6.0` |
| **MAJOR** | **you, by hand** | Large update or breaking change: `.\build.ps1 -SetVersion 1.0.0` |

Never hand-edit the number in `VERSION`: use `-SetVersion` so every generated
artefact is regenerated from it in the same step.

```powershell
.\build.ps1                       # 0.5.3 -> 0.5.4, stamped into everything
.\build.ps1 -NoVersionBump        # build without consuming a version (iterating)
.\build.ps1 -SetVersion 0.6.0     # explicit bump, e.g. raising the minor
```

The version is **only written back after the whole build succeeds**, and via a
temp file + move, so a failed or interrupted build never burns or half-writes a
version.

### How it reaches each artefact

`build.ps1` writes these before anything compiles:

| Generated file | Consumed by |
| --- | --- |
| `build\generated\Version.props` | root `Directory.Build.props` → the .NET app (`Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`) |
| `build\generated\Version.h` | C++ projects — `DS_VERSION_MAJOR/MINOR/PATCH`, `DS_VERSION_STR`, `DS_VERSION_WSTR`, `DS_VERSION_COMMA`, `DS_VERSION_STR4`. Plain `#define`s only, so it is includable from both `.cpp` and `.rc`. |
| `build\generated\version.txt` | scripts and CI (bare number, no BOM, no newline) |

The root `Directory.Build.props` is picked up automatically by
`app\DesktopSplitter.csproj` with no edit to that project. Everything in it is
guarded on `'$(MSBuildProjectExtension)' == '.csproj'` so the C++ `.vcxproj`
builds are untouched.

In the installer the version drives the Add/Remove Programs `DisplayVersion`,
the wizard's displayed version, and the exe's own version resource.

CI builds with `-NoVersionBump`: nothing there could commit the change back, so
bumping would only cause version drift and a dirty tree. The workflow reads the
version from the build step's output (falling back to `version.txt`); a `v*`
tag still wins for release naming.

> The driver INF's `DriverVer` is deliberately **not** tied to this version — it
> keeps its date/time stamping, which is what the validated driver upgrade path
> depends on.

## Build

```powershell
# Everything, in dependency order, with versioning (recommended)
.\build.ps1

# --- or individual projects ---
# Compositor + devcreate (plain VS2022)
msbuild compositor\dscomp.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild installer\devcreate\devcreate.vcxproj /p:Configuration=Release /p:Platform=x64

# Control app
dotnet build app\DesktopSplitter.csproj -c Release

# Driver (requires WDK)
msbuild driver\DesktopSplitterVdd.vcxproj /p:Configuration=Release /p:Platform=x64
```

Use a "Developer PowerShell for VS 2022" prompt so `msbuild` is on PATH.

### Local unsigned driver build (validated 2026-10-19)

This workspace has a project-local toolchain under ignored
`artifacts/toolchain/`, installed without elevation or system-wide WDK changes:

- Official NuGet `Microsoft.Windows.WDK.x64` **10.0.26100.6584** and its
  `Microsoft.Windows.SDK.CPP` / `.x64` dependencies **10.0.26100.1**.
- Microsoft's WDK VS build extension **18.9.11915.232**, extracted and overlaid
  onto a local copy of the installed VC MSBuild targets. The wrapper selects
  the WDK's available VS 17.0 build tasks explicitly.
- Official MSVC **14.44.35207** x64 Desktop and OneCore Spectre libraries,
  extracted from the installed Visual Studio catalog's packages after SHA-256
  verification. The compiler itself comes from the existing VS installation.

Microsoft documents the [WDK NuGet installation route](https://learn.microsoft.com/en-us/windows-hardware/drivers/install-the-wdk-using-nuget).
To repeat the build on this prepared workspace:

```powershell
.\tools\build-driver-local.ps1 -Configuration Release
.\tools\build-driver-local.ps1 -Configuration Debug
```

The wrapper discovers Visual Studio with `vswhere` (or accepts
`-VisualStudioPath`), limits compilation concurrency, and uses the Windows 10
19041 target definitions, UMDF 2.25 and IddCx 1.4. Both configurations passed
C++20 compilation, static analysis and Universal package signability checks.
The generated unsigned DLL/INF/CAT are under
`driver/x64/<Configuration>/DesktopSplitterVdd/`.

Signing is explicitly disabled (`SignMode=Off`, `EnableTestSign=false`). This
command only builds; it does not trust certificates, install drivers, create
devices or change display settings. It requires the prepared local files and
does not download or install missing prerequisites automatically. A fresh
machine needs the dependencies above or the normal full WDK installation.
ARM64 and actual Windows 10 21H2/Windows 11 operation have not been tested.

## Global shortcuts

The main window's **단축키 · 화면 배치와 선택 칸** panel lets the user edit eleven
global shortcuts. They default off. Enable **전역 단축키 사용**, edit/capture
the chords, then press **단축키 설정 저장**. Saved shortcuts work while the app
is in the tray and are released on window/application shutdown.

| Action | Default |
| --- | --- |
| Swap the focused virtual desktop into layout slot 1 / 2 / 3 / 4 | Ctrl+Alt+Shift+F1 / F2 / F3 / F4 |
| Select fixed layout slot 1 / 2 / 3 / 4 for merge focus | Ctrl+Alt+Shift+F5 / F6 / F7 / F8 |
| Open app | Ctrl+Alt+Shift+F9 |
| Apply current edited layout | Ctrl+Alt+Shift+F10 |
| Revert | Ctrl+Alt+Shift+F11 |

Numbers refer to the editor's fixed layout slots, rather than Windows DISPLAYn
suffixes. Ctrl+Alt+Shift+F1 swaps the desktop containing the focused window with the desktop
currently in slot 1; all its contents follow, while individual windows are not
repositioned with window APIs. Repeating the same destination is a no-op. Physical
displays outside this split and nonexistent slots are rejected.

Whole-desktop swaps use one CCD position-only SetDisplayConfig batch and live
compositor routing. Existing source/target modes, path priority, refresh, rotation
and primary identity are retained; no SDC_ALLOW_CHANGES or SAVE_TO_DATABASE is
used (see [Microsoft's SetDisplayConfig reference](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig)).
Windows validates before applying. Differently sized sources recompute grid
tracks without changing resolutions; overlap with other displays is rejected.
The compositor reuses capture threads, textures and cursor textures and reads one
atomic slot permutation per frame. A versioned, bounded window-message protocol
works with both regular and message-only compositor windows. It verifies the
config fingerprint and uses compare-and-exchange to reject stale updates. A failed
transaction attempts to restore its routing and CCD snapshot and reports any
rollback failure. There is no startup replay or persistent swap order: the next
explicit split application starts with the editor's original slot order.
Window movement follows the current live slot mapping after a swap.

The new compositor must be started by a user-requested split application before
desktop swaps are available; older running compositors are detected before any
CCD mutation. Merely opening the new control app does not restart the compositor.
Each binding can be changed or disabled. No key combination can guarantee freedom
from application-level conflicts. F12 remains excluded per the Windows
[RegisterHotKey documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey).

Old numbered-key and app-control defaults migrate to the function-key defaults in memory;
custom chords and disabled actions are preserved. The migration is versioned so
subsequent explicit customizations are not rewritten. If a new default conflicts
with a preserved custom chord, that new action starts disabled. Individual actions
can be unchecked and saved. Window/app actions are in collapsed subpanels. Previous
and next movement and the laptop preset have been removed. Legacy action IDs 3/4
stay reserved for serialization only and are never displayed or registered.

Input fields temporarily unregister the saved hotkeys while editing. Invalid,
duplicate, F12 and compositor-reserved Ctrl+Alt+Shift+S chords are rejected.
Native registration uses MOD_NOREPEAT; conflicts are isolated per action and
displayed on the failed row while other keys remain registered. No conflicting
key is stolen. Reconfiguration allocates new message IDs so stale
queued hotkeys cannot execute against the new configuration. Settings persistence
failure restores the previous registrations. Applying a split preserves shortcut
preferences. Apply/Revert retain the existing validation and confirmation flow.
Deactivating the app restores saved keys even if a textbox retains logical focus.
App-open bypasses the busy-operation gate and requests normal foreground
activation, reporting an OS refusal instead of forcing focus. Received actions
are recorded in the existing log; there is no test-mode UI.

Window movement directly targets only active DesktopSplitter virtual monitors by
their current layout slots. It reads fresh Win32 monitor areas rather
than cached WinForms screens, normalizes device names, and uses foreground-window
placement. Maximized moves restore/place/maximize in that order, including
restoring maximization if placement fails, and fit normal bounds to the
destination. It does not change display topology or modes. Desktop shell,
taskbar, tool windows and the app itself are excluded. Native hotkey/placement
APIs predate Windows 10 21H2; actual cross-OS/window/DPI and CCD swap behavior still
needs interactive validation. No native hotkeys, CCD changes, live compositor
control messages or window moves are exercised by the
automated test:

```powershell
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release -- --shortcuts
```

This uses a fake registrar, in-memory settings and pure placement calculations
for direct target routing, migration, partial conflicts, per-action opt-out,
stale messages, edit pause, save failure, cleanup, maximized operation order,
reserved/duplicate keys, negative coordinates and taskbars. `--hiding-ui` also
renders the compiled shortcut panel without executing commands or registering
global keys. Diagnostic hosts never register saved global shortcuts.

`ui-smoke --shortcut-diagnostics` prints saved settings merged with new defaults
and the current target map without registering keys or moving windows. During
the 2026-10-20 review, driver GET_STATUS and the target map both reported zero
active virtual monitors; actual key reception, window activation and movement
were deliberately not exercised on the user's desktop.

## Charcoal appearance and bundled assets

The app uses a fixed opaque charcoal palette (#202225 background, layered dark
gray controls, soft gray text and muted blue-gray accents), independently of the
Windows light/dark preference. High contrast still uses the library's accessible
palette. Main, confirmation and hiding windows share the palette, embedded
NanumSquareRound fonts and the same icon. The title bar requests dark rendering
for the app's own HWND only; no Windows-wide setting is written.

The Regular and Bold TTF files are unmodified official NAVER downloads. WPF loads
them from assembly resources; the WinForms tray menu uses process-private font
registrations released on shutdown. No fonts are installed into Windows. The
official license and notice ship in Assets/Fonts; the installer also places them
beside the executable as NanumSquareRound-LICENSE/NOTICE.txt. See [NAVER's font catalog](https://hangeul.naver.com/fonts/search?f=nanum)
and [font license](https://help.naver.com/service/30016/contents/18088?osType=PC&lang=ko).

`Assets/App.ico` contains 16, 20, 24, 32, 48, 64, 128 and 256 pixel versions of the
original tray design. The EXE's Win32 icon, WPF window icons and tray icon all use
this asset. Regenerate it with `tools/build-app-icon.ps1`. `ui-smoke --appearance`
checks embedded Korean glyphs, menu fonts, icon sizes, compiled styles and
high-contrast switching offscreen without running display actions.

References: [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey),
[WINDOWPLACEMENT coordinates](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-windowplacement).

## Install (dev machine)

### Prepared workspace: first install without applying a split

From an elevated PowerShell, run `installer/install-local-driver.ps1`. This
workspace-specific helper uses the prepared x64 Release package, SDK 26100
signing tool, local WDK Inf2Cat and built `devcreate.exe`. It refuses an existing
device to avoid replaying a persisted configuration during an upgrade.

It copies the package into `artifacts/driver-install/<timestamp>/package`,
creates/reuses a non-exportable local development code-signing key, trusts the
public certificate in LocalMachine Root and TrustedPublisher, signs the DLL,
**regenerates the catalog after DLL signing**, signs the catalog and verifies
the catalog against both DLL and INF before staging it and creating the device.
It does not run the app, send SET_CONFIG, change boot policy or request a reboot.
`result.json` and `install.log` record the outcome and certificate thumbprint.
The older separate `sign-dev.ps1`/`install-driver.ps1` instructions below are
legacy; use the combined helper for this prepared workspace.

After installation, this read-only command checks the app's driver interface:

```powershell
dotnet run --project app/tools/ui-smoke/ui-smoke.csproj -c Release -- --driver-status
```

On 2026-10-19 the local Windows 11 build 26200 installation succeeded as
`ROOT\DISPLAY\0000`, published INF `oem6.inf`, device problem code 0. A subsequent
GET_STATUS from a non-elevated process returned protocol version 1 and **zero
active virtual monitors**. No split was applied. This is installation/startup
validation on that machine, not Windows 10 21H2 compatibility certification.

### Legacy separate signing and installation

No test-signing mode and no Secure Boot changes are needed:
DesktopSplitterVdd is a UMDF (user-mode) driver, so Windows' kernel-mode
signing policy does not apply. The package only needs a signature the machine
trusts, which `sign-dev.ps1` arranges locally.

1. Sign the driver with a dev cert and trust it machine-wide (elevated):
   ```powershell
   installer\sign-dev.ps1
   ```
2. Install driver + create the virtual device (elevated):
   ```powershell
   installer\install-driver.ps1
   ```
3. Run `app\bin\Release\net10.0-windows\DesktopSplitter.exe`, pick a layout,
   Apply.

## Uninstall

```powershell
installer\uninstall-driver.ps1
```

## Production signing (shipping to other machines)

The dev-cert route works only on machines where the certificate was installed.
To distribute properly — silent install on any Windows 10/11 machine, Secure
Boot on, nothing added to certificate stores — use **Microsoft attestation
signing** via the Hardware Dev Center:

1. Register for the [Windows Hardware Developer Program](https://learn.microsoft.com/windows-hardware/drivers/dashboard/register-for-the-hardware-program)
   in Partner Center (identity verification requires an EV code-signing
   certificate or Microsoft's current accepted equivalent — check the page).
2. Package `DesktopSplitterVdd.inf/.dll/.cat` into a `.cab`, sign the cab with
   the EV certificate.
3. Submit on the hardware dashboard, select **attestation signing** and the
   target Windows versions (Windows 10 1809+ / Windows 11 x64).
4. Download the Microsoft-signed package and ship it; `pnputil /add-driver`
   then works everywhere with no cert or boot-config changes.

Attestation signing requires the driver to pass the "Universal" signability
checks — this build already does (verified in the build output). Full WHQL
(HLK testing) is only needed for Windows Update distribution.
