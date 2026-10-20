using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>Live adapters are reached only from explicit user operations, never startup/exit recovery.</summary>
public static class SplitMatrixWorkspace
{
    public static MatrixSnapshot? Capture(IProgress<string> log)
    {
        var settings = SettingsStore.Load();
        if (settings is not { RememberMatrixWindows: true }) return null;
        if (string.IsNullOrWhiteSpace(settings.PhysicalMonitorPath)) {
            log.Report("이전 빌드의 분할은 원본 모니터 식별 기록이 없어 창을 추측해 가져오지 않습니다. 새 빌드에서 분할한 뒤부터 매트릭스를 기억합니다.");
            return null;
        }
        using var mutation = ApplyService.DisplayOperationGate.Enter();
        var monitors = DisplayConfig.Enumerate();
        if (!monitors.Any(monitor => monitor.IsDeskSplitVirtual)) return null;
        try {
            var targets = SplitPrimarySelector.ReadTargets(monitors);
            if (targets.Count != settings.Segments.Count) throw new InvalidOperationException("저장된 분할 칸 수와 현재 연결이 다릅니다.");
            int primary = SplitPrimarySelector.ReadChoice(settings, settings.PhysicalMonitorPath, settings.Layout, targets.Count);
            var workspace = new MatrixWorkspace(new MatrixWindowSystem());
            var memory = workspace.Capture(settings.PhysicalMonitorPath, settings.Layout, primary, targets);
            var removed = new List<MatrixSnapshot>();
            SettingsStore.Update(current => {
                removed.AddRange(current.MatrixMemories.Where(old => MatrixWorkspace.Matches(old, memory.PhysicalMonitorPath, memory.Layout, memory.SlotCount)));
                current.MatrixMemories.RemoveAll(old => removed.Contains(old));
                current.MatrixMemories.Add(memory);
                while (current.MatrixMemories.Count > 8) { removed.Add(current.MatrixMemories[0]); current.MatrixMemories.RemoveAt(0); }
            });
            foreach (var old in removed) workspace.Release(old, memory);
            log.Report($"이 그룹의 매트릭스 기억: {memory.Windows.Count}개 대표 창 · 우선 칸 {primary}번.");
            return memory;
        } catch (Exception error) {
            log.Report("대표 창을 기억하지 못했습니다. 화면 복원은 계속합니다: " + error.Message);
            // A failed capture must not leave an older pre-swap snapshot eligible for re-split.
            try {
                var discarded = new List<MatrixSnapshot>();
                SettingsStore.Update(current => discarded.AddRange(DiscardUnverifiedMemory(current, settings.PhysicalMonitorPath, settings.Layout)));
                var workspace = new MatrixWorkspace(new MatrixWindowSystem());
                foreach (var old in discarded) workspace.Release(old, null);
            } catch (Exception cleanupError) { log.Report("이전 창 배치 기억을 지우지 못했습니다: " + cleanupError.Message); }
            return null;
        }
    }

    public static IReadOnlyList<MatrixSnapshot> DiscardUnverifiedMemory(AppSettings settings, string physicalPath, LayoutKind layout)
    {
        var discarded = settings.MatrixMemories.Where(memory => string.Equals(memory.PhysicalMonitorPath, physicalPath, StringComparison.OrdinalIgnoreCase)
            && memory.Layout == layout).ToArray();
        settings.MatrixMemories.RemoveAll(discarded.Contains);
        return discarded;
    }

    public static MatrixRestorePlan? PrepareRestore(string physicalPath, LayoutKind layout, IProgress<string> log)
    {
        var settings = SettingsStore.Load();
        if (settings is not { RememberMatrixWindows: true }) return null;
        var memory = settings.MatrixMemories.LastOrDefault(old => MatrixWorkspace.Matches(old, physicalPath, layout, LayoutDefinition.FromKind(layout).SegmentCount));
        if (memory is null) return null;
        try {
            var monitors = DisplayConfig.Enumerate();
            var allowed = monitors.Where(monitor => !monitor.IsDeskSplitVirtual && string.Equals(monitor.MonitorDevicePath, physicalPath, StringComparison.OrdinalIgnoreCase))
                .Select(monitor => monitor.DeviceName).ToList();
            if (string.Equals(settings.PhysicalMonitorPath, physicalPath, StringComparison.OrdinalIgnoreCase))
                allowed.AddRange(SplitPrimarySelector.ReadTargets(monitors).Select(target => target.DeviceName));
            // Decide before topology changes: a remembered app manually moved to another monitor stays there.
            return new MatrixWorkspace(new MatrixWindowSystem()).Prepare(memory, allowed);
        } catch (Exception error) { log.Report("이 그룹의 대표 창 복원 준비를 건너뛰었습니다: " + error.Message); return null; }
    }

    public static void RestoreSplit(MatrixRestorePlan? plan, int primary, IProgress<string> log, CancellationToken ct)
    {
        if (plan is null) return;
        using var mutation = ApplyService.DisplayOperationGate.Enter(ct);
        try {
            var settings = SettingsStore.Load() ?? throw new InvalidOperationException("실행 중인 분할 설정이 없습니다.");
            var targets = SplitPrimarySelector.ReadTargets(DisplayConfig.Enumerate());
            var destination = new MatrixDestination(settings.PhysicalMonitorPath, settings.Layout, false, targets);
            var result = new MatrixWorkspace(new MatrixWindowSystem()).Restore(plan, destination, primary, ct);
            Report(result, log, "분할 매트릭스");
        } catch (OperationCanceledException) { throw; }
        catch (Exception error) { log.Report("분할은 적용했지만 대표 창 복원은 건너뛰었습니다: " + error.Message); }
    }

    public static void RestoreMerged(MatrixSnapshot? memory, IProgress<string> log, CancellationToken ct)
    {
        if (memory is null || !MatrixWorkspace.IsValid(memory)) return;
        using var mutation = ApplyService.DisplayOperationGate.Enter(ct);
        try {
            var monitors = DisplayConfig.Enumerate();
            var physical = monitors.SingleOrDefault(monitor => !monitor.IsDeskSplitVirtual
                && string.Equals(monitor.MonitorDevicePath, memory.PhysicalMonitorPath, StringComparison.OrdinalIgnoreCase));
            if (physical is null) throw new InvalidOperationException("합친 원본 모니터의 연결을 확인하지 못했습니다.");
            var area = SplitWindowMover.ReadAreas().Single(item => SplitWindowMover.NormalizeDevice(item.DeviceName) == SplitWindowMover.NormalizeDevice(physical.DeviceName));
            var targets = Enumerable.Range(1, memory.SlotCount).Select(slot => new SplitTarget(slot, area.DeviceName, area.Bounds, area.Work)).ToArray();
            var plan = new MatrixRestorePlan(memory, memory.Windows.Select(window => window.Identity).ToHashSet());
            var destination = new MatrixDestination(physical.MonitorDevicePath, memory.Layout, true, targets);
            var result = new MatrixWorkspace(new MatrixWindowSystem()).Restore(plan, destination, memory.PrimarySlot, ct);
            Report(result, log, "원본 화면");
        } catch (OperationCanceledException) { throw; }
        catch (Exception error) { log.Report("화면은 합쳤지만 대표 창 정리는 건너뛰었습니다: " + error.Message); }
    }

    private static void Report((int Placed, int Skipped, bool Focused) result, IProgress<string> log, string destination)
    {
        log.Report($"{destination}: 대표 창 {result.Placed}개 배치 요청 · 닫힘·최소화·권한 제한 등으로 {result.Skipped}개 건너뜀.");
        if (result.Placed > 0 && !result.Focused) log.Report("선택 칸의 창이 없거나 Windows가 전경 전환을 허용하지 않았습니다. 항상 위 고정은 사용하지 않습니다.");
    }
}
