# Windows compatibility and .NET 10 migration

## Required baseline

- Windows 10 **21H2, build 19044**, x64.
- Windows 11, x64.
- Compatibility on both operating systems takes priority over optional visual effects.

The .NET 10 UI targets `net10.0-windows`, rather than a Windows 11-specific
target. WPF UI 4.3.0 supplies Fluent controls and themes while the application
retains its WPF/Win32 interop, native window chrome, PerMonitorV2 DPI manifest,
and existing driver protocol. Mica/Acrylic are not enabled. The theme manager
always requests `WindowBackdropType.None`.

Microsoft's .NET 10 support table includes Windows 10 21H2 Enterprise and IoT
editions. Support lifecycle and runtime compatibility are different questions:
this project also requires validation on the intended Home/Pro 21H2 edition
if that is a deployment target. Do not claim Microsoft support for those editions.

References:
- [.NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [WPF UI 4.3.0](https://github.com/lepoco/wpfui/releases/tag/4.3.0)

## Validation status (2026-10-19)

| Check | Result |
| --- | --- |
| .NET 10 Release build on Windows 11 Pro 25H2 | Passed, no warnings/errors |
| Self-contained win-x64 app publish | Passed |
| Local application startup | Main window initialized and process responded; display buttons not exercised |
| Light, dark and high-contrast XAML checks | Passed offscreen; safety-dialog event wiring also checked |
| Windows 10 21H2 installation and runtime | **Pending: requires a Windows 10 21H2 test machine/VM** |
| Driver and compositor integration on either OS after migration | **Pending: WDK/driver installation and an explicit display test session** |

An offscreen render or a successful build does not prove driver/display
compatibility. The driver and compositor implementations were not changed by
this migration. The experimental specialized-display hiding path still has
additional edition/API restrictions and is not a baseline requirement.

## Cross-OS acceptance checks

Run these only in an explicitly authorized test session; normal development
and CI UI checks must not manipulate the user's display or steal focus.

1. On Windows 10 21H2 and Windows 11, install the complete signed product and
   start the app. A self-contained release must not require a separate .NET install.
2. Check native window operations, keyboard navigation, dropdown readability,
   100/150/200% DPI and mixed-DPI displays, light/dark/high-contrast themes.
3. On a dedicated display test setup, validate each split layout, manual sizes,
   divider dragging, Keep/Revert and automatic confirmation expiry.
4. Check fractional refresh, multi-monitor placement, restoring the original
   primary display, lock/unlock, sleep/resume and hot-plug recovery.
5. Check tray startup, exit/restore, upgrade and uninstall. Test optional
   display hiding separately on an edition that supports it.

## Code review fixes (2026-10-19)

1. **Windows 11-only task submission.** The SDK declares `IDisplayTaskPool2`
   (`TryExecuteTask`) in UniversalApiContract v13. The compositor previously
   invoked it unconditionally. It now queries the actual object for that
   interface once and uses the original `ExecuteTask` on systems without it,
   including Windows 10 21H2. Modern present-status feedback is preserved when
   available; legacy HRESULT failures still propagate to runtime recovery.
   Failed submissions are never retried through the other interface, avoiding
   duplicate tasks. Legacy submission does not provide modern per-present status;
   this remains a real-hardware validation item.
2. **Undeployed C++ runtime.** Release compositor and EDID helper projects used
   `/MD`, but the installer did not carry their VC++ runtime DLLs or install the
   redistributable. They now use `/MT`, matching the existing installer and
   device-node helper. PE import checks confirm all three native payload
   executables have no direct MSVCP/VCRUNTIME/CONCRT DLL dependency. Driver
   linkage is unchanged. Static runtime security updates require rebuilding
   and redistributing these executables with a serviced toolchain.
3. **Unsafe optional synchronization.** Specialized presentation previously
   continued when a shared scanout fence was unavailable and ignored a failed
   fence `Signal`. Initialization now requires both the shared fence and
   `ID3D11DeviceContext4`; signal errors follow the existing runtime-failure
   path. Unsupported graphics stacks fail acquisition and use the existing
   fallback/rollback flow instead of starting unsynchronized scanout.

Validation without display operations:

- Release compositor and EDID helper rebuilt successfully.
- Geometry probe: **64 passed**.
- Presenter probe: **48 passed**, including real COM test doubles with/without
  the newer task-pool interface, refusal feedback, and HRESULT propagation.
- `tools/check-native-dependencies.ps1`: all three payload binaries passed.
- No product startup, display acquisition, driver installation, registry writes,
  focus changes, or screen configuration changes were performed for this review.

The native probes and dependency audit are included in CI. These tests do not
replace Windows 10 21H2 device testing. The required baseline remains unchanged.

Reference: [DisplayTaskPool API](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core.displaytaskpool)
and the installed SDK's `winrt/windows.devices.display.core.h` contract declarations.

## C++20 migration validation (2026-10-19)

- All eight `.vcxproj` files now select C++20; no C++17 standard selection remains.
- Release builds passed for the compositor, device-node helper, EDID helper and
  all three probes. The compositor and presenter probe also built in Debug;
  the Debug presenter probe passed all 48 checks.
- The installer's ten C++ translation units passed a compile-only check using
  the actual project settings. The check used a temporary targets override
  under `artifacts/` to omit payload staging; no installer was linked or run.
- Driver x64 Release and Debug builds subsequently passed using WDK NuGet
  10.0.26100.6584 and MSVC 14.44.35207, with C++20, Spectre mitigation,
  `/W4 /WX`, and driver code analysis enabled. Both unsigned packages passed
  Universal signability checks without warnings and generated DLL/INF/CAT.
- WDF cleanup lambdas were replaced with named, function-class-annotated
  callbacks. Known diagnostics inside the WDF headers (C4471/C6553) and the
  STL `make_shared` implementation (C28197) are suppressed only around those
  external includes; checks remain enabled for the driver's own code.
- The project-local WDK and Spectre libraries live in ignored
  `artifacts/toolchain/`; `tools/build-driver-local.ps1` repeats the unsigned
  x64 build without elevation or installation. ARM64 compilation and actual
  Windows 10 21H2/Windows 11 driver operation remain untested. Full installer
  linking remains dependent on the signed driver payload.

## Korean UI and read-only status review (2026-10-19)

- Main UI, layouts, segment validation, tray actions, confirmation dialogs,
  startup preferences and common status messages now use Korean. Monitor/layout
  selection is on the left, editing on the right, with persistent Apply/Revert
  actions and a collapsible log. The canvas scales down without changing pixel
  dimensions. Advanced hiding/startup controls are grouped separately.
- Manual review now performs the initial **read-only** hiding probe independently
  of crash recovery. Capability, actual-hidden state and saved intent are shown
  separately. Failed/unsupported probes report unknown; users can cancel the
  wait, and blocked native probes cannot accumulate new worker threads.
  This does not unhide or restore a display.
- Applying checks the driver interface, GET_STATUS protocol and compositor
  executable before entering the mutation pipeline. The service repeats this
  check before capturing state or stopping an existing compositor. Missing
  drivers yield an actionable Korean message without display changes; buttons
  remain subject to ordinary editing/validation rules.
- `ui-smoke --state-checks` covers capability versus actual-hidden state,
  intent, refresh, unsupported/failed queries, cancellation and bounded worker count,
  plus missing-driver preflight rejection. No SET IOCTL or display operation is
  called. Light/dark/high-contrast render checks cover 1180×880 and 960×680;
  headless lifecycle tests cover close cancellation, exit and review modes.
- These diagnostic checks neither install nor replace the driver. Actual
  splitting requires an installed and trusted driver package; these UI checks
  do not validate Windows 10/11 hardware behavior.

## Further review priorities

### Experimental details crash fix (2026-10-19)

The crash dumps showed repeated layout exceptions opening nested global error
dialogs until stack overflow. A compiled-view reproduction identified a two-way
`Run.Text` binding to the read-only `StatusRowViewModel.Glyph` property when the
hiding details expander was opened. Both status runs now specify OneWay. The
previous inert rendering fixture rewrote bindings and had no status rows, which
hid this defect. A new compiled panel test expands, collapses and updates actual
read-only status rows in all three themes without executing display commands.

The dispatcher handler marks exceptions handled before notification; its reporter
blocks nested dialogs, throttles identical errors for 30 seconds and records the
full diagnostic under `%LOCALAPPDATA%/DesktopSplitter/logs/ui-errors.log`. The
test host's queued product startup path is explicitly suppressed. The first
reproduction exposed this additional test-host defect; subsequent panel tests
assert unchanged settings and active-monitor count. Physical hiding remains a
separate, unperformed hardware test.

### Windows 10 driver discovery, FHD modes and matrix transitions (2026-10-20)

The previous managed CM interface-list flag was `1`, which is
`CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES`, not PRESENT (`0`) in the Windows SDK.
Discovery now enumerates active interfaces, retries only the API's
`CR_BUFFER_SMALL` resize race, and falls back to SetupDi PRESENT/device-interface
enumeration. Connection tries all unique candidates using read-only GET_STATUS
handshakes, closes failed handles, validates protocol/counts, and distinguishes no
active interface from access/IO/version failures. There is no cached or machine-
specific driver path, automatic installation or device-enable action.
If no active interface exists, read-only PnP diagnostics report the device's
started state/problem code, including disabled and signature/load failures,
instead of always calling the driver uninstalled.
See Microsoft's [device interface enumeration guidance](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/enumerating-installed-device-interface-classes).

App and driver now accept positive dimensions up to the D3D11 texture API ceiling
16384, replacing the arbitrary 640..7680 policy. A particular GPU/Windows display
stack can impose stricter real resource limits. All presets at 1920x1080,
1366x768 and 1280x720 pass managed validation; native tests cover 40 timing/EDID
cases including 960x540 and tiny/large modes. Zero dimensions/rates, fixed protocol
array bounds, integer/rational overflow and texture bounds remain checked. The
23..1000Hz policy cap is removed. Protocol v2 requires the rebuilt driver, so a
v1 driver cannot claim compatibility then reject FHD modes under the old floor.

Matrix preparation queries remembered HWNDs directly rather than rescanning the
desktop, and capture caches process metadata only within that one enumeration.
Pre-transition user-minimized windows stay excluded; eligible windows minimized
by Windows during topology changes can be restored. A single asynchronous
WINDOWPLACEMENT request carries normal bounds and final maximized state.
Window restore runs before keep/revert confirmation; decline, cancellation or
failure compensates once, including the prior window plan. The compositor signals
a PID-specific readiness event only after initialization; the app waits on that,
process exit or cancellation, with the existing configurable safety deadline for
failed initialization. Frame pacing also wakes on renderer stop, including very
low refresh rates, without a forced shutdown delay.

Managed regression checks (`--win10-fixes`, matrix/live-sync and existing safety
suites), native ModeProbe and WDK compilation do not replace Windows 10 runtime
testing. The subsequent isolated VM run below covers installation, loading and
mode creation; physical GPU composition/window transitions remain a hardware test.

### Windows 10 reflector installation defect (2026-10-20)

The INF previously included `WUDFRD.inf`/`WUDFRD.NT*`. Microsoft documents that
this inbox INF exists only on Windows 11, despite WUDFRd.sys and UMDF themselves
being available on Windows 10. Consequently, a Windows 10 installation could
fail before exposing any device interface; discovery fixes cannot repair this.
The INF now registers the inbox WUDFRd service explicitly with AddService
0x1fa, preserving existing service settings. This documented mechanism works on
Windows 10 and Windows 11; the OS-owned WUDFRd.sys is never bundled or copied.
Current InfVerif emits warning 2084 for this inbox binary lacking a package
CopyFiles entry; adding a private OS binary copy to silence it would be wrong.
Inf2Cat signability passes. The minimum INF build is corrected to 18362 because
the binary requires IddCx 1.4 without a 1.3 runtime fallback. Supported/test
target remains Windows 10 21H2 (19044), which supplies IddCx 1.5 and UMDF 2.31;
the driver's requirements are IddCx 1.4 and UMDF 2.25.

Reference: [Microsoft reflector installation guidance](https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/adding-the-reflector).

`powershell.exe -NoProfile -File tools\Test-DriverConnection.ps1 -OutputPath report.json`
runs a standalone read-only probe on either OS, without the app/.NET runtime or
administrator access. It records OS build, product hardware-ID matched PnP
devices/problem codes, installed package identity, active device interfaces and
raw GET_STATUS responses (including old protocol 1). Only GET_STATUS is sent;
it never applies/clears a configuration or installs/enables/restarts a device.
The current Windows 11 host reports PnP problem 0 and a successful protocol 1
GET_STATUS response. The new app requires protocol 2, so the old installed
driver must be updated separately. This host probe is not a Windows 10 test.
A Windows 10 21H2 VM can check installation/load/discovery in isolation; actual
physical-GPU split rendering and window transitions still need hardware review.

### Windows 10 21H2 VM validation (2026-10-21)

An offline Hyper-V generation 2 VM running Microsoft's Windows 10 Enterprise
LTSC 2021 Evaluation (21H2/build 19044) completed **25 checks with zero failures**.
Secure Boot stayed enabled; the VM had 4 GiB RAM, two capped virtual processors,
no network adapter and no physical GPU assignment. The VM was shut down after
testing. The host driver/version and display configuration were not changed.

Verified in the guest:

- Shipping installer wizard self-test, installation, repeat installation and
  package verification, all exit 0.
- Active interface discovery/open/GET_STATUS: protocol 2; adapter diagnostics
  state 2 (ready), NTSTATUS 0.
- Four 960x540 monitor arrivals at 60 Hz, followed by removal back to count 0.
- Compositor binary loading (`--help`) and device helper status.
- All 16 managed regression modes, plus native driver-status probing.
- The actual self-contained shipping app opening and closing successfully with
  `--manual-review` and the guest's documented close-to-tray setting disabled.

The clean machine exposed two further compatibility defects:

1. Opening certificate stores with OPEN_EXISTING failed on a not-yet-created
   TrustedPublisher store. Addition now permits store creation; removal still
   opens existing stores only. Publisher is added before Root. A trusted
   self-signed development root also repairs missing publisher registration
   after an earlier partial installation. Installation/reinstallation then
   succeeded in the guest without changing its boot/signing policy.
2. The default .NET 9+ CET-compatible host aborted before Main on this early
   21H2 image: `Your Windows doesn't fully support CET` / 0x80131506. Managed
   hosts now publish with CETCompat=false, following Microsoft's
   [documented app-level option](https://learn.microsoft.com/en-us/dotnet/core/compatibility/interop/9.0/cet-support).
   This trades away this application's CET opt-in to support the requested
   baseline; it does not modify Windows-wide exploit protection. A published
   PE-header regression guard checks the actual single-file host flag.

The driver also exposes an optional read-only GET_DIAGNOSTICS extension (12
bytes/version 1), retaining the v2 configuration/status ABI. It reports adapter
initialization separately from device/interface availability. Completed adapter
initialization failures reject positive configurations before persistence,
instead of accepting a queue that can never drain; zero-monitor clearing still
cancels pending work. The app surfaces the native failure status before applying.
Older v2 drivers without the optional query remain compatible.

Scripts and guard conditions live in `tools/win10-lab/`. Mutating guest tests
require the expected VM computer name, Microsoft VM model, build 19044 and a
per-lab marker. The ordinary `Test-DriverConnection.ps1` remains read-only.
The test runner owns process handles to avoid PS 5.1 losing exit codes. Test
deadlines are configurable harness limits, not product display timers.

The official ISO SHA256 was
`E4AB2E3535BE5748252A8D5D57539A6E59BE8D6726345EE10E7AFD2CB89FEFB5`.
Local results: `artifacts/win10-21h2-validation/summary.json`,
`fhd-roundtrip.json`, `driver-after-tests.json` and `vm-environment.json`.

### Hardware review checklist

1. Validate actual GPU capture/composition, physical monitor transitions and
   remembered application windows on Windows 10/11 hardware. VM monitor arrival
   tests and fake-window regressions do not establish physical rendering speed.
2. Reconcile outdated driver-signing instructions in `ARCHITECTURE.md` and
   `install-driver.ps1` with the current signing/installer path.
3. Validate rollback after driver/compositor failures and display topology changes.
4. Expand adaptive layout and accessibility coverage once real cross-OS results
   establish a baseline.

### v3 / nine-output follow-up (2026-10-21)

A separate v3 package (INF 0.38.43.65) passed 27 guest steps on build 19044,
including native 4 → 0 → 9 → 0 monitor counts; adapter diagnostics ended at
Ready / STATUS_SUCCESS. See `artifacts/nine-version-fix-validation/` and
`docs/FREE-LAYOUT.md` for the identical-DLL INF-version A/B evidence.
This does not yet prove active OS geometry or nine-source visual composition.
The final product-version package (0.5.6.0), upgrade binding fix, and actual
3×3 composition harness await an isolated guest run. Existing user VMs are
explicitly excluded. The new VM start was blocked by the host memory reserve;
no running user VM was reconfigured or stopped to make room.
