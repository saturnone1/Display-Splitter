using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public static partial class ApplyService
{
    // ------------------------------------------------------------------ display hiding

    private static MonitorInfo? FindLive(string deviceName)
        => ReadMonitors().FirstOrDefault(m => SameDevice(m.DeviceName, deviceName));

    /// <summary>
    /// Starts the compositor, first specializing the physical monitor when that was asked for.
    ///
    /// Sequence: SET specialization ON -> launch dscomp --specialized -> wait for
    /// Global\DeskSplitSpecializedLive -> keep/revert countdown. Anything short of a confirmed
    /// keep rolls all the way back to a plain windowed compositor, so a failure can never leave
    /// the monitor off the desktop with nothing drawing to it.
    /// </summary>
    private static (Process Compositor, string SpecializedDevice) LaunchCompositor(
        ApplyRequest request, IProgress<string> log, CancellationToken ct)
    {
        if (!request.HidePhysicalDisplay) return (StartPlain(log), string.Empty);

        MonitorInfo? physical = FindLive(request.Physical.DeviceName);
        if (physical is not null && !SameMonitorTarget(request.Physical, physical))
            throw new ApplyException("모니터 식별 정보가 바뀌어 숨김 적용을 중단했습니다.");
        if (physical is null)
        {
            log.Report($"WARNING: {request.Physical.DeviceName} disappeared; starting the compositor normally.");
            return (StartPlain(log), string.Empty);
        }

        // Windows only allows a NON-primary display to be specialized.
        if (physical.IsPrimary)
        {
            log.Report($"WARNING: {physical.DisplayLabel} is still the primary display, and Windows only " +
                       "allows non-primary displays to be hidden. Skipping hiding.");
            return (StartPlain(log), string.Empty);
        }

        if (!MonitorSpecialization.TryGet(physical, out SpecializationState state, out int rc))
        {
            log.Report($"WARNING: could not read monitor specialization (error {rc}). Skipping hiding.");
            return (StartPlain(log), string.Empty);
        }

        if (!state.Available)
        {
            log.Report($"WARNING: {physical.DisplayLabel} cannot be hidden (GET raw 0x{state.RawValue:X}: " +
                       $"monitor={state.AvailableForMonitor}, system={state.AvailableForSystem}). Skipping hiding.");
            return (StartPlain(log), string.Empty);
        }

        bool alreadyHidden = state.EnabledNow;
        if (alreadyHidden)
            log.Report($"{physical.DisplayLabel} is already off the desktop — going straight to acquiring it.");

        // START THE COMPOSITOR FIRST. It polls until the display becomes specialized and then
        // acquires it. If the display were removed from the desktop before anything was ready to
        // scan out to it, the panel would simply go black — which is exactly what happened when
        // this was done in the other order.
        using var liveSignal = CompositorLauncher.PrepareReadinessSignal();
        Process compositor = CompositorLauncher.Start(
            CompositorLauncher.SpecializedArgument,
            CompositorLauncher.WaitForTargetArgument, CompositorLauncher.WaitForTargetSeconds.ToString(),
            CompositorLauncher.FrameDeadlineArgument, request.ConfirmationSeconds.ToString());
        bool retained = false;
        try
        {
        log.Report($"Started {compositor.ProcessName}.exe {CompositorLauncher.SpecializedArgument} " +
                   $"{CompositorLauncher.WaitForTargetArgument} {CompositorLauncher.WaitForTargetSeconds} " +
                   $"(pid {compositor.Id}) — it is now waiting to take over the display.");

        // Journal ownership before hiding: a failed/cancelled apply must still know what to undo.
        SettingsStore.Update(settings => {
            settings.SpecializedDevice = physical.DeviceName;
            settings.SpecializedDevicePath = physical.MonitorDevicePath;
        });
        if (!alreadyHidden && !SetSpecialized(physical, true, request.SpecializationUi, log, ct))
        {
            log.Report("모니터 숨김을 취소해 일반 화면 합성 방식으로 다시 시작합니다.");
            StopCompositor(log, ct);
            if (!MonitorSpecialization.TryGet(physical, out SpecializationState afterCancel, out int queryError))
                throw new ApplyException($"숨김 취소 후 모니터 상태를 확인하지 못했습니다 (오류 {queryError}).");
            if (afterCancel.EnabledNow) RestoreToDesktop(physical, request.SpecializationUi, log, CancellationToken.None);
            return (StartPlain(log), string.Empty);
        }

        log.Report($"Waiting up to {request.ConfirmationSeconds}s for " +
                   $"{CompositorLauncher.SpecializedLiveEventName}...");
        CompositorLauncher.SpecializedWait wait =
            CompositorLauncher.WaitForSpecializedLive(compositor, liveSignal, TimeSpan.FromSeconds(request.ConfirmationSeconds), ct, log.Report);

        bool keep = ShouldKeepHiding(
            wait,
            askUser: () => request.SpecializationUi is null
                           || DisplayOperationGate.Outside(() => request.SpecializationUi.ConfirmKeep(physical, request.ConfirmationSeconds, ct)),
            log);

        if (keep) { retained = true; return (compositor, physical.DeviceName); }

        // Roll back: stop the compositor, put the display back, restart windowed.
        log.Report("모니터 숨김을 되돌립니다…");
        StopCompositor(log, ct);
        RestoreToDesktop(physical, request.SpecializationUi, log, ct);

        return (StartPlain(log), string.Empty);
        }
        finally
        {
            if (!retained)
            {
                try { if (!compositor.HasExited && !CompositorLauncher.SignalStop()) compositor.CloseMainWindow(); }
                catch (InvalidOperationException) { }
                compositor.Dispose();
            }
        }
    }

    /// <summary>
    /// The keep-or-roll-back decision, isolated so it can be tested without touching a display.
    ///
    /// The user is only ever asked when the compositor actually confirmed it is presenting
    /// through the hidden display. Every other outcome — no signal, or the compositor dying —
    /// rolls back WITHOUT prompting, because in those cases the user may be looking at a blank
    /// screen and cannot answer.
    /// </summary>
    public static bool ShouldKeepHiding(
        CompositorLauncher.SpecializedWait wait, Func<bool> askUser, IProgress<string> log)
    {
        switch (wait)
        {
            case CompositorLauncher.SpecializedWait.Live:
                log.Report("숨긴 모니터의 화면 출력을 확인했습니다. 설정 유지 여부를 선택해 주세요.");
                bool keep = askUser();
                log.Report(keep ? "모니터 숨김을 유지합니다." : "모니터를 바탕 화면으로 복원합니다.");
                return keep;

            case CompositorLauncher.SpecializedWait.CompositorExited:
                log.Report("The compositor stopped before it could present through the hidden display — " +
                           "rolling back without prompting.");
                return false;

            default:
                log.Report($"No {CompositorLauncher.SpecializedLiveEventName} signal before the blank-screen safety deadline — " +
                           "rolling back without prompting.");
                return false;
        }
    }

    /// <summary>
    /// Adds or removes a monitor from the desktop, trying the routes in order of preference:
    ///
    ///   1. SystemSettingsAdminFlows.exe SpecializeDisplay — what Windows itself runs. Needs a
    ///      validated identity hash, so it is off until that has been proven on this machine.
    ///   2. The guided Windows Settings dialog, which the user completes by hand.
    ///
    /// The in-process DisplayConfig type-13 SET is deliberately NOT attempted: disassembly of every
    /// System32 binary that calls DisplayConfigSetDeviceInfo shows none of them ever builds the
    /// public 0x138-byte packet, so it is a metadata-only definition that cannot succeed.
    /// Returns true once the display actually reached the requested state.
    /// </summary>
    private static bool SetSpecialized(
        MonitorInfo physical, bool enable, ISpecializationUi? ui, IProgress<string> log, CancellationToken ct)
    {
        string what = enable ? "Removing" : "Restoring";
        log.Report($"{what} {physical.DisplayLabel} {(enable ? "from" : "to")} the desktop...");

        // Keep user actions serialized while allowing lock/UAC primary reconciliation.
        AdminFlowResult admin = DisplayOperationGate.Outside(() => SystemSettingsAdminFlowsClient.TryRun(physical, enable, log, ct));
        if (admin.Success)
        {
            log.Report(admin.Message);
            return DisplayStateWaiter.Wait(() =>
            {
                if (!MonitorSpecialization.TryGet(physical, out SpecializationState state, out int error))
                    throw new ApplyException($"모니터 숨김 상태를 확인하지 못했습니다 (오류 {error}).");
                return state.EnabledNow;
            }, current => current == enable, ct) == enable;
        }

        if (admin.NotAttempted)
            log.Report($"자동 변경을 사용할 수 없습니다: {admin.Message}");
        else
            log.Report("자동 변경 실패: " + admin.Message);

        log.Report("Windows 설정에서 변경하는 방법을 안내합니다.");

        if (ui is null)
        {
            log.Report($"Do it yourself: Settings > System > Display > Advanced display > " +
                       $"select {physical.DisplayLabel} > turn {(enable ? "ON" : "OFF")} " +
                       "\"Remove display from desktop\".");
            return false;
        }

        // While the user works the toggle, watch Windows' own elevated helper so we can learn
        // this monitor's identity hash and do it in one click next time.
        using var watcher = new SpecializationCaptureWatcher(physical, log);
        watcher.Start();

        var failure = new SpecializationResult(false, 0, admin.Message);
        bool reached;
        if (enable)
        {
            reached = DisplayOperationGate.Outside(() => ui.GuideManualEnable(physical, failure, ct));
        }
        else
        {
            DisplayOperationGate.Outside(() => { ui.GuideManualDisable(physical, failure, ct); return true; });
            reached = MonitorSpecialization.TryGet(physical, out SpecializationState after, out _)
                      && !after.EnabledNow;
        }

        SpecializeDisplayInvocation? captured = watcher.StopAndCollect();
        if (captured is not null)
        {
            SpecializationHashStore.Learn(physical, captured.Hash, log);
        }
        else if (watcher.ElevationRequired)
        {
            log.Report("Note: Windows ran its display helper, but DesktopSplitter could not read how " +
                       "it was called because that helper runs as administrator. Run DesktopSplitter " +
                       "as administrator once while you do this, and it will remember the setting and " +
                       "be able to do it for you in one click from then on.");
        }

        return reached;
    }

    /// <summary>Puts a monitor back on the desktop. Never leaves the user without a way out.</summary>
    private static void RestoreToDesktop(
        MonitorInfo physical, ISpecializationUi? ui, IProgress<string> log, CancellationToken ct)
    {
        if (SetSpecialized(physical, false, ui, log, ct))
            log.Report($"{physical.DisplayLabel}이 바탕 화면에 복원되었습니다.");
        else
            throw new ApplyException($"{physical.DisplayLabel} 복원을 확인하지 못해 후속 화면 변경을 중단했습니다. Windows 디스플레이 설정에서 숨김 해제를 확인해 주세요.");
    }

    private static Process StartPlain(IProgress<string> log)
    {
        Process compositor = CompositorLauncher.Start();
        log.Report($"Started {compositor.ProcessName}.exe (pid {compositor.Id}).");
        return compositor;
    }

    /// <summary>
    /// Puts a monitor we specialized back on the desktop. Used by Revert, by failure cleanup and
    /// by startup recovery after a crash.
    /// </summary>
    public static void ClearSpecializationIfOurs(
        IProgress<string> log, ISpecializationUi? ui = null, CancellationToken ct = default)
    {
        AppSettings? settings = SettingsStore.Load();
        string device = settings?.SpecializedDevice ?? string.Empty;
        if (string.IsNullOrEmpty(device)) return;

        MonitorInfo? monitor = !string.IsNullOrEmpty(settings?.SpecializedDevicePath)
            ? DisplayConfig.FindConnectedTarget(settings.SpecializedDevicePath)
            : FindLive(device); // Legacy settings may only contain the GDI name.
        if (monitor is null)
        {
            throw new ApplyException($"복원 대상 {device}의 장치 식별 정보를 확인하지 못해 후속 화면 변경을 중단했습니다. 복구 기록을 유지합니다. 해당 모니터 연결과 Windows의 숨김 설정을 확인해 주세요.");
        }
        if (!MonitorSpecialization.TryGet(monitor, out SpecializationState state, out int error))
            throw new ApplyException($"복원할 모니터 상태를 확인하지 못했습니다 (오류 {error}). 복구 기록을 유지합니다.");
        if (state.EnabledNow)
        {
            log.Report($"Restoring {monitor.DisplayLabel} to the desktop...");
            RestoreToDesktop(monitor, ui, log, ct);
        }

        if (settings is not null)
        {
            SettingsStore.Update(current => {
                if (SameDevice(current.SpecializedDevice, device)) {
                    current.SpecializedDevice = string.Empty;
                    current.SpecializedDevicePath = string.Empty;
                }
            });
        }
    }

    /// <summary>
    /// Called at startup: if we recorded a hidden display but no split is running, a previous
    /// session died without cleaning up. Put the display back.
    /// </summary>
    public static void RecoverStaleSpecialization(IProgress<string> log, CancellationToken ct = default)
    {
        using var operation = UserOperationGate.Enter(ct);
        using var mutation = DisplayOperationGate.Enter(ct);
        AppSettings? settings = SettingsStore.Load();
        if (settings is null || string.IsNullOrEmpty(settings.SpecializedDevice)) return;
        if (HidingStatus.IsSplitActive()) return;

        log.Report($"Recovering from a previous session: {settings.SpecializedDevice} was left hidden.");
        ClearSpecializationIfOurs(log, ct: ct);
    }

}
