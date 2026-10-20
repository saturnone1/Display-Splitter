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

### Remaining hardware review

1. Establish the Windows 10 21H2 test environment before claiming compatibility.
2. Reconcile outdated driver-signing instructions in `ARCHITECTURE.md` and
   `install-driver.ps1` with the current signing/installer path.
3. Validate rollback after driver/compositor failures and display topology changes.
4. Expand adaptive layout and accessibility coverage once real cross-OS results
   establish a baseline.
