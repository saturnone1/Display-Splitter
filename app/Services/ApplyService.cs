using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>
/// Orchestrates the full apply / revert sequence described in docs/ARCHITECTURE.md.
/// All methods are synchronous and blocking; call them off the UI thread and pass an
/// <see cref="IProgress{T}"/> created on the UI thread for status updates.
/// </summary>
public static partial class ApplyService
{
    public const int MinSegmentDimension = LayoutGeometry.MinSegmentDimension;
    public const int MaxSegmentDimension = LayoutGeometry.MaxSegmentDimension;


    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>%ProgramData%\DesktopSplitter</summary>
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DesktopSplitter");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    // ------------------------------------------------------------------ apply

    /// <summary>Read-only preflight. Must run before stopping a compositor or changing displays.</summary>
    public static void CheckPrerequisites()
    {
        DESKSPLIT_STATUS status = DriverClient.GetStatus();
        if (status.Version != DriverClient.DESKSPLIT_PROTOCOL_VERSION)
            throw new DriverIoException("드라이버와 앱의 통신 버전이 다릅니다. 같은 버전의 드라이버가 필요합니다.");
        if (!CompositorLauncher.TryFindExecutable(out _))
            throw new CompositorNotFoundException("화면 합성 프로그램(dscomp.exe)을 찾을 수 없습니다. 빌드 또는 설치 위치를 확인해 주세요.");
    }

    /// <summary>
    /// Applies a split. The mutating pipeline runs under <see cref="DisplayOperationGate"/> so an
    /// automatic suspend/resume cannot interleave with it; the confirmation countdown runs OUTSIDE
    /// the gate so a UAC prompt is never made to wait on it.
    /// </summary>
    public static ApplyResult Apply(ApplyRequest request, IProgress<string> log, CancellationToken ct)
        => ApplyInternal(request, log, ct, recoverFailures: true);

    private static ApplyResult ApplyInternal(ApplyRequest request, IProgress<string> log, CancellationToken ct, bool recoverFailures)
    {
        using var operation = UserOperationGate.Enter(ct);
        AppliedSnapshot snapshot = AppliedSnapshot.None;
        ApplyResult applied;
        using (DisplayOperationGate.Enter(ct))
        {
            applied = recoverFailures
                ? PipelineRecovery.Run(markChanged => ApplyCore(request, log, ct, markChanged, out snapshot), () =>
                {
                    request.State?.Report(DisplayOperationState.Restoring);
                    log.Report("적용이 중단되어 이전 화면 상태로 복원합니다.");
                    RollBackTo(snapshot, log, CancellationToken.None, request.SpecializationUi);
                })
                : ApplyCore(request, log, ct, () => { }, out snapshot);
        }

        // g. "Keep these display settings?" — only for user-initiated applies. A change that
        //    leaves the user unable to see or click anything undoes itself when the countdown
        //    lapses, exactly like Windows' own resolution prompt.
        if (!request.PromptToConfirm || request.Confirmer is null) return applied;

        string summary = $"{request.Physical.DisplayLabel} · {request.Layout.Name} " +
                         $"({string.Join(", ", request.Resolutions.Select(r => $"{r.Width}x{r.Height}"))})";

        bool confirmed = PipelineRecovery.Run(changed =>
        {
            request.State?.Report(DisplayOperationState.AwaitingConfirmation);
            changed(); // The display change is already live, even if opening the dialog fails.
            return request.Confirmer.ConfirmApply(summary, request.ConfirmationSeconds, ct);
        }, () => {
            request.State?.Report(DisplayOperationState.Restoring);
            RollBackTo(snapshot, log, CancellationToken.None, request.SpecializationUi);
        });
        if (confirmed)
        {
            log.Report("변경한 설정을 유지합니다.");
            return applied;
        }

        log.Report("설정 유지를 확인하지 않아 이전 화면으로 복원합니다.");
        request.State?.Report(DisplayOperationState.Restoring);
        string restored = RollBackTo(snapshot, log, CancellationToken.None, request.SpecializationUi);
        return new ApplyResult
        {
            VirtualMonitors = applied.VirtualMonitors,
            ConfigPath = applied.ConfigPath,
            CompositorPid = applied.CompositorPid,
            RolledBack = true,
            RollbackDescription = restored,
        };
    }

    private static ApplyResult ApplyCore(
        ApplyRequest request, IProgress<string> log, CancellationToken ct, Action markChanged, out AppliedSnapshot snapshot)
    {
        ct.ThrowIfCancellationRequested();
        request.State?.Report(DisplayOperationState.Applying);
        ValidateRequest(request);
        CheckPrerequisites();

        MonitorInfo physical = request.Physical;
        LayoutDefinition layout = request.Layout;
        int segmentCount = layout.SegmentCount;

        // Captured BEFORE anything changes, so a declined confirmation can put it all back.
        snapshot = CaptureAppliedSnapshot();
        IReadOnlyList<MonitorInfo> before = DisplayConfig.Enumerate();
        (string previousPrimaryDevice, string previousPrimaryName) = CapturePreviousPrimary(before, log);

        // 0. A compositor from a previous run must not be holding duplication handles on
        //    monitors we are about to tear down.
        if (CompositorLauncher.IsRunning())
        {
            log.Report("기존 화면 합성기를 먼저 중지합니다…");
            markChanged();
            StopCompositor(log, ct);
        }

        // Restore a previously hidden output before replacing its compositor/topology.
        if (!string.IsNullOrEmpty(SettingsStore.Load()?.SpecializedDevice))
        {
            markChanged();
            ClearSpecializationIfOurs(log, request.SpecializationUi, ct);
        }

        // a. Build and send DESKSPLIT_CONFIG.
        DESKSPLIT_CONFIG cfg = DriverClient.NewConfig();
        cfg.MonitorCount = (uint)segmentCount;
        for (int i = 0; i < segmentCount; i++)
        {
            (int w, int h) = request.Resolutions[i];
            cfg.Monitors[i].ModeCount = 1;
            cfg.Monitors[i].PreferredModeIndex = 0;
            cfg.Monitors[i].Modes[0] = new DESKSPLIT_MODE
            {
                Width = (uint)w,
                Height = (uint)h,
                RefreshMillihertz = physical.MaxRefreshMillihertz,
            };
        }

        log.Report($"드라이버에 분할 설정 전송 (IOCTL 0x{DriverClient.IOCTL_DESKSPLIT_SET_CONFIG:X8}) · " +
                   $"화면 {segmentCount}개 · {physical.MaxRefreshMillihertz} mHz…");
        try
        {
            ct.ThrowIfCancellationRequested();
            markChanged();
            DriverClient.SetConfig(cfg);
        }
        catch (DriverNotFoundException ex)
        {
            throw new ApplyException(ex.Message, ex);
        }
        catch (DriverIoException ex)
        {
            throw new ApplyException(ex.Message, ex);
        }

        // b. Wait for the virtual monitors to arrive.
        log.Report($"가상 모니터 {segmentCount}개의 연결 신호를 기다리는 중… 취소할 수 있습니다.");
        IReadOnlyList<MonitorInfo> arrived = WaitForVirtualMonitors(segmentCount, ct);
        foreach (MonitorInfo m in arrived)
        {
            log.Report($"  Found {m.DeviceName} ({m.FriendlyName})" +
                       (m.Edid is null ? "  [no EDID]" : $"  [{m.Edid}]"));
        }

        // Map segment i -> the monitor the driver created as DESKSPLIT_CONFIG.Monitors[i],
        // identified by the EDID serial the driver stamps (0xD5000001 + index).
        IReadOnlyList<MonitorInfo> virtualMonitors = MapSegmentsToMonitors(arrived, segmentCount, log, ct);

        // c. Arrange them contiguously to the right of the physical monitor in one batch.
        //    If the selected physical monitor owned Windows primary, segment 1 takes that role.
        //    An original primary on a different physical monitor is preserved. This policy
        //    is independent of the matrix representative, which only controls app-window ordering.
        //    Windows restores the layout it remembers for this set of monitors as they arrive
        //    (typically segment 1 already primary and the physical monitor moved left of it),
        //    so positions are computed from where the physical monitor is NOW.
        log.Report("가상 모니터의 화면 배치를 설정합니다…");
        IReadOnlyList<MonitorInfo> current = DisplayConfig.Enumerate();
        MonitorInfo physicalNow = current.FirstOrDefault(m => SameDevice(m.DeviceName, physical.DeviceName))
            ?? throw new ApplyException($"{physical.DeviceName} is no longer active, so the split cannot be placed next to it.");
        if (!SameMonitorTarget(physical, physicalNow)
            || physical.CurrentWidth != physicalNow.CurrentWidth || physical.CurrentHeight != physicalNow.CurrentHeight)
            throw new ApplyException("적용 도중 모니터 연결 또는 해상도가 바뀌어 배치를 중단했습니다. 현재 모니터를 다시 선택해 주세요.");
        IReadOnlyList<DisplayArranger.VirtualPlacement> placements =
            DisplayArranger.ComputePlacements(physicalNow, layout, virtualMonitors, request.Resolutions);
        // Windows primary routing is independent of the matrix's representative slot.
        string appliedPrimaryDevice = WindowsPrimaryForSplit(physical, before, virtualMonitors);
        DisplayArranger.ApplyLayout(
            DisplayArranger.ComposeLayout(current, placements, physical.MaxRefreshHz, appliedPrimaryDevice),
            log.Report);

        // Re-read so config.json records what Windows actually settled on, keeping the
        // segment -> monitor mapping established above.
        var refreshed = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (MonitorInfo m in ReadMonitors()) refreshed[m.DeviceName] = m;
        virtualMonitors = virtualMonitors
            .Select(v => refreshed.TryGetValue(v.DeviceName, out MonitorInfo? cur) ? cur : v)
            .ToList();

        // d. Write %ProgramData%\DesktopSplitter\config.json.
        // physRects mirror the editor: track sizes are normalised onto the monitor's real
        // dimensions (last track absorbs the rounding remainder) rather than being an equal
        // subdivision, so a dragged/typed split lands on screen exactly where it was drawn.
        IReadOnlyList<PixelRect> physRects = LayoutGeometry.ComputePhysRects(
            layout, physical.CurrentWidth, physical.CurrentHeight, request.Resolutions);
        log.Report("실제 모니터의 분할 영역: " +
                   string.Join("  ", physRects.Select((r, i) => $"[{i + 1}] {r}")));
        var compositorConfig = new CompositorConfig
        {
            PhysicalDevice = physical.DeviceName,
            RefreshMillihertz = physical.MaxRefreshMillihertz,
        };
        for (int i = 0; i < segmentCount; i++)
        {
            (int w, int h) = request.Resolutions[i];
            compositorConfig.Segments.Add(new CompositorSegment
            {
                VirtualDevice = virtualMonitors[i].DeviceName,
                Width = w,
                Height = h,
                PhysRect = CompositorRect.From(physRects[i]),
            });
        }
        WriteCompositorConfig(compositorConfig);
        log.Report($"분할 설정 저장: {ConfigPath}");

        // e. Optionally hide the physical monitor, then launch the compositor.
        //    Ordering matters: the display must already be specialized when dscomp starts, and
        //    only a NON-primary display can be specialized — which is exactly why this runs
        //    after segment 1 was promoted to primary in step c2.
        (Process compositor, string specializedDevice) = LaunchCompositor(request, log, ct);
        using var compositorLifetime = compositor;

        // f. Remember the settings for next launch / tray "Apply last".
        SettingsStore.Update(settings =>
        {
            settings.PhysicalDevice = physical.DeviceName;
            settings.PhysicalMonitorPath = physical.MonitorDevicePath;
            settings.PrimarySegmentSlot = request.PrimarySlot;
            settings.MatrixPrimarySlots[SplitPrimarySelector.ProfileKey(physical.MonitorDevicePath, layout.Kind)] = request.PrimarySlot;
            settings.PhysicalFriendlyName = physical.FriendlyName;
            settings.Layout = layout.Kind;
            settings.Segments = request.Resolutions.Select(r => new SegmentSize { Width = r.Width, Height = r.Height }).ToList();
            settings.PreviousPrimaryDevice = previousPrimaryDevice;
            settings.PreviousPrimaryFriendlyName = previousPrimaryName;
            settings.AppliedPrimaryDevice = appliedPrimaryDevice;
            settings.HidePhysicalDisplay = request.HidePhysicalDisplay;
            settings.SpecializedDevice = specializedDevice;
            settings.SpecializedDevicePath = string.IsNullOrEmpty(specializedDevice) ? string.Empty : physical.MonitorDevicePath;
            settings.LastAppliedUtc = DateTime.UtcNow;
            settings.SplitActiveOnExit = true;
        });
        log.Report($"사용자 설정 저장: {SettingsStore.FilePath}");

        return new ApplyResult
        {
            VirtualMonitors = virtualMonitors,
            ConfigPath = ConfigPath,
            CompositorPid = compositor.Id,
        };
    }

    /// <summary>
    /// Records that no split is running. <paramref name="deliberate"/> distinguishes a user
    /// pressing Revert (never auto-apply again) from an automatic teardown at exit.
    /// </summary>
    public static void MarkSplitInactive(bool deliberate)
    {
        try
        {
            AppSettings? settings = SettingsStore.Load();
            if (settings is null) return;
            if (!settings.SplitActiveOnExit && !deliberate) return;

            SettingsStore.Update(current => {
                current.SplitActiveOnExit = false;
                if (deliberate) current.ApplyOnStartup = false;
            });
        }
        catch
        {
            // Never let bookkeeping break a revert.
        }
    }

    /// <summary>
    /// Decides whether startup should re-apply the saved configuration. Auto-apply happens when
    /// the user asked for it, or when a split was still running when we last exited — but never
    /// after a deliberate revert, and never when a split is somehow already up.
    /// </summary>
    public static bool ShouldAutoApplyOnStartup(AppSettings? settings, bool splitAlreadyActive)
    {
        if (settings is null) return false;
        if (splitAlreadyActive) return false;
        if (string.IsNullOrEmpty(settings.PhysicalDevice) || settings.Segments.Count == 0) return false;
        return settings.ApplyOnStartup || settings.SplitActiveOnExit;
    }

    // ------------------------------------------------------------------ confirm / roll back

    /// <summary>
    /// What is live right now, so a declined apply can be put back. "Applied" means a split is
    /// genuinely running, not merely that settings.json remembers one — a user who reverted
    /// deliberately must not have it resurrected.
    /// </summary>
    public static AppliedSnapshot CaptureAppliedSnapshot()
    {
        AppSettings? settings = SettingsStore.Load();
        if (settings is null || string.IsNullOrEmpty(settings.PhysicalDevice) || settings.Segments.Count == 0)
            return AppliedSnapshot.None;
        if (!HidingStatus.IsSplitActive())
            return AppliedSnapshot.None;

        return new AppliedSnapshot(
            true,
            settings.PhysicalDevice,
            settings.Layout,
            settings.Segments.Select(s => (s.Width, s.Height)).ToList(),
            settings.HidePhysicalDisplay) { PrimarySlot = Math.Clamp(settings.PrimarySegmentSlot, 1, settings.Segments.Count) };
    }

    /// <summary>
    /// Restores the configuration captured before the declined apply — re-applying the previous
    /// split, or reverting completely when there was none. Never prompts (that would trap the
    /// user in a loop of countdowns).
    /// </summary>
    private static string RollBackTo(
        AppliedSnapshot snapshot, IProgress<string> log, CancellationToken ct, ISpecializationUi? ui)
    {
        if (!snapshot.WasApplied)
        {
            log.Report("이전 분할 설정이 없어 원래 화면으로 복원합니다.");
            Revert(log, ct, ui, deliberate: false);
            return "no split (fully reverted)";
        }

        MonitorInfo? physical = FindLive(snapshot.PhysicalDevice);
        LayoutDefinition layout = LayoutDefinition.FromKind(snapshot.Layout);

        if (physical is null || snapshot.Resolutions.Count != layout.SegmentCount)
        {
            log.Report("이전 분할 설정을 사용할 수 없어 원래 화면으로 복원합니다.");
            Revert(log, ct, ui, deliberate: false);
            return "no split (fully reverted)";
        }

        log.Report($"이전 분할 설정 복원: {physical.DisplayLabel} · {layout.Name}");
        ApplyInternal(new ApplyRequest
        {
            Physical = physical,
            Layout = layout,
            Resolutions = snapshot.Resolutions,
            PrimarySlot = snapshot.PrimarySlot,
            HidePhysicalDisplay = snapshot.HidePhysicalDisplay,
            SpecializationUi = ui,
            PromptToConfirm = false,
        }, log, ct, recoverFailures: false);

        return $"{layout.Name} on {physical.DisplayLabel}";
    }

    // ------------------------------------------------------------------ revert

    public static void Revert(IProgress<string> log, CancellationToken ct, ISpecializationUi? ui = null, bool deliberate = true)
    {
        using var operation = UserOperationGate.Enter(ct);
        using (DisplayOperationGate.Enter(ct))
        {
            RevertCore(log, ct, ui, deliberate);
        }
    }

    private static void RevertCore(IProgress<string> log, CancellationToken ct, ISpecializationUi? ui, bool deliberate)
    {
        log.Report("화면 합성기를 중지합니다…");
        StopCompositor(log, ct);
        log.Report("화면 합성기를 중지했습니다.");

        // Put the physical monitor back on the desktop BEFORE anything else is torn down —
        // otherwise the primary restore and the virtual-monitor removal both operate on a
        // desktop that is missing a display.
        ClearSpecializationIfOurs(log, ui, ct);

        // Hand primary back to a physical monitor BEFORE the virtual ones disappear. If the
        // primary vanishes, Windows picks an arbitrary survivor and re-bases the desktop on
        // its own terms, which scrambles window positions.
        RestorePrimaryBeforeTeardown(log);

        log.Report("드라이버에 가상 모니터 제거 요청을 전송합니다…");
        try
        {
            DriverClient.ClearConfig();
        }
        catch (DriverNotFoundException ex)
        {
            throw new ApplyException(ex.Message, ex);
        }
        catch (DriverIoException ex)
        {
            throw new ApplyException(ex.Message, ex);
        }

        DisplayStateWaiter.Wait(DisplayConfig.Enumerate, monitors => monitors.All(m => !m.IsDeskSplitVirtual), ct);

        MarkSplitInactive(deliberate);

        log.Report("복원 완료: 가상 모니터를 제거했습니다.");
    }

    // ------------------------------------------------------------------ helpers

    public static void ValidateRequest(ApplyRequest request)
    {
        if (request.PrimarySlot < 1 || request.PrimarySlot > request.Layout.SegmentCount)
            throw new ApplyException("주 화면은 현재 분할 칸 중에서 선택해야 합니다.");
        if (request.HidePhysicalDisplay && string.IsNullOrWhiteSpace(request.Physical.MonitorDevicePath))
            throw new ApplyException("숨길 모니터의 장치 경로를 확인하지 못했습니다. 안전하게 복원할 수 있도록 숨김 적용을 중단합니다.");
        if (request.ConfirmationSeconds <= 0 || TimeSpan.FromSeconds(request.ConfirmationSeconds).TotalMilliseconds >= uint.MaxValue)
            throw new ApplyException("안전 확인 시간은 0보다 커야 합니다.");
        if (request.PromptToConfirm && request.Confirmer is null)
            throw new ApplyException("설정 유지 확인 창을 사용할 수 없어 적용을 중단했습니다.");
        if (request.Resolutions.Count != request.Layout.SegmentCount)
        {
            throw new ApplyException(
                $"Layout '{request.Layout.Name}' needs {request.Layout.SegmentCount} segment resolutions, " +
                $"got {request.Resolutions.Count}.");
        }
        if (request.Layout.SegmentCount > DriverClient.DESKSPLIT_MAX_MONITORS)
        {
            throw new ApplyException(
                $"Layout '{request.Layout.Name}' needs {request.Layout.SegmentCount} monitors but the driver " +
                $"supports at most {DriverClient.DESKSPLIT_MAX_MONITORS}.");
        }
        for (int i = 0; i < request.Resolutions.Count; i++)
        {
            (int w, int h) = request.Resolutions[i];
            if (w < MinSegmentDimension || w > MaxSegmentDimension ||
                h < MinSegmentDimension || h > MaxSegmentDimension)
            {
                throw new ApplyException(
                    $"Segment {i + 1} resolution {w}x{h} is out of range " +
                    $"({MinSegmentDimension}..{MaxSegmentDimension}).");
            }
        }
        if (request.Physical.IsDeskSplitVirtual)
            throw new ApplyException("가상 모니터가 선택되어 있습니다. 실제 모니터를 선택해 주세요.");
    }

    /// <summary>
    /// Orders the arrived virtual monitors so that index i is the monitor the driver created
    /// from DESKSPLIT_CONFIG.Monitors[i], using the EDID serial (0xD5000001 + index) that the
    /// driver stamps. Unreadable, incomplete or ambiguous identities fail closed.
    /// </summary>
    private static IReadOnlyList<MonitorInfo> MapSegmentsToMonitors(
        IReadOnlyList<MonitorInfo> arrived, int segmentCount,
        IProgress<string> log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (TryMapByEdid(arrived, segmentCount, out List<MonitorInfo> mapped, out string reason))
        {
            log.Report("EDID 일련번호를 기준으로 분할 영역과 가상 모니터를 연결했습니다.");
            for (int i = 0; i < mapped.Count; i++)
            {
                log.Report($"  Segment {i + 1} -> {mapped[i].DeviceName} (driver index {mapped[i].DeskSplitMonitorIndex})");
            }
            return mapped;
        }
        throw new ApplyException($"분할 화면의 식별 정보를 확인하지 못해 배치를 중단했습니다: {reason}. 화면 번호를 추측해서 적용하지 않습니다.");
    }

    public static bool TryMapByEdid(
        IReadOnlyList<MonitorInfo> arrived, int segmentCount,
        out List<MonitorInfo> mapped, out string reason)
    {
        var byIndex = new Dictionary<int, MonitorInfo>();
        bool duplicates = false;

        foreach (MonitorInfo m in arrived)
        {
            int index = m.DeskSplitMonitorIndex;
            if (index < 0 || index >= segmentCount) continue;
            if (!byIndex.TryAdd(index, m)) duplicates = true;
        }

        bool complete = !duplicates
                        && byIndex.Count == segmentCount
                        && Enumerable.Range(0, segmentCount).All(byIndex.ContainsKey);

        if (complete)
        {
            mapped = Enumerable.Range(0, segmentCount).Select(i => byIndex[i]).ToList();
            reason = string.Empty;
            return true;
        }

        mapped = new List<MonitorInfo>();
        reason = duplicates
            ? "two virtual monitors reported the same driver index"
            : byIndex.Count == 0
                ? "no EDID serial in the DesktopSplitter range could be read"
                : $"only {byIndex.Count} of {segmentCount} monitors reported a usable driver index";
        return false;
    }

    private static IReadOnlyList<MonitorInfo> WaitForVirtualMonitors(
        int expected, CancellationToken ct)
    {
        return DisplayStateWaiter.Wait(() => DisplayConfig.Enumerate()
                .Where(m => m.IsDeskSplitVirtual)
                .OrderBy(m => DisplayConfig.DeviceIndex(m.DeviceName))
                .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList(), found => found.Count == expected && TryMapByEdid(found, expected, out _, out _), ct);
    }

    private static void StopCompositor(IProgress<string> log, CancellationToken cancellationToken)
    {
        if (!CompositorLauncher.StopAndWait(cancellationToken, log.Report))
            throw new ApplyException("합성기의 정상 종료를 확인하지 못해 디스플레이 변경을 중단했습니다.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static IReadOnlyList<MonitorInfo> ReadMonitors()
    {
        return DisplayConfig.Enumerate();
    }

    /// <summary>
    /// Writes config.json to %ProgramData%\DesktopSplitter. There is deliberately no
    /// fallback location: the compositor's contract is a single fixed path.
    /// </summary>
    public static void WriteCompositorConfig(CompositorConfig config)
    {
        try
        {
            AtomicFile.WriteText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new ApplyException(
                $"Access denied writing {ConfigPath}.{Environment.NewLine}" +
                "Run DesktopSplitter as administrator once — it will create " +
                $"{ConfigDirectory} and grant the current user write access; " +
                "subsequent runs do not need elevation.", ex);
        }
        catch (IOException ex)
        {
            throw new ApplyException($"Could not write {ConfigPath}: {ex.Message}", ex);
        }
    }
}

