using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public static class SplitPrimarySelector
{
    public static int FindPrimarySlot(IReadOnlyList<SplitTarget> targets, string primaryDevice)
        => targets.SingleOrDefault(target => SplitWindowMover.NormalizeDevice(target.DeviceName) == SplitWindowMover.NormalizeDevice(primaryDevice))?.Number ?? 0;

    public static string ProfileKey(string physicalPath, LayoutKind layout)
        => physicalPath.ToUpperInvariant() + "|" + (int)layout;

    public static int ReadChoice(AppSettings? settings, string physicalPath, LayoutKind layout, int count)
        => Math.Clamp(settings?.MatrixPrimarySlots.GetValueOrDefault(ProfileKey(physicalPath, layout), 1) ?? 1, 1, Math.Max(1, count));

    public static void Choose(AppSettings settings, string expectedPhysicalPath, LayoutKind expectedLayout, int slot, int count)
    {
        if (string.IsNullOrWhiteSpace(expectedPhysicalPath)
            || !string.Equals(settings.PhysicalMonitorPath, expectedPhysicalPath, StringComparison.OrdinalIgnoreCase) || settings.Layout != expectedLayout)
            throw new InvalidOperationException("현재 선택한 모니터·분할 방식과 실행 중인 분할 그룹이 다릅니다. 다른 그룹의 선택 칸은 변경하지 않았습니다.");
        if (!LayoutDefinition.HasSegmentCount(expectedLayout, count) || slot < 1 || slot > count)
            throw new InvalidOperationException("현재 그룹에 없는 분할 칸입니다.");
        settings.PrimarySegmentSlot = slot;
        settings.MatrixPrimarySlots[ProfileKey(expectedPhysicalPath, expectedLayout)] = slot;
    }

    public static IReadOnlyList<SplitTarget> ReadTargets(IReadOnlyList<MonitorInfo> monitors)
    {
        var virtuals = monitors.Where(monitor => monitor.IsDeskSplitVirtual).ToArray();
        if (virtuals.Length == 0) return Array.Empty<SplitTarget>();
        if (virtuals.Any(monitor => monitor.DeskSplitMonitorIndex < 0)
            || virtuals.Select(monitor => monitor.DeskSplitMonitorIndex).Distinct().Count() != virtuals.Length)
            throw new InvalidOperationException("분할 화면 식별 정보가 불명확해 주 화면 변경을 중단했습니다.");
        return SplitDisplayReorder.ResolveTargets(SplitWindowMover.BuildTargets(monitors, SplitWindowMover.ReadAreas()), requireLiveRouting: true);
    }

    public static SplitTarget Select(int slot, string expectedPhysicalPath, LayoutKind expectedLayout, CancellationToken ct)
    {
        using var operation = ApplyService.UserOperationGate.Enter(ct);
        using var mutation = ApplyService.DisplayOperationGate.Enter(ct);
        var monitors = DisplayConfig.Enumerate();
        var targets = ReadTargets(monitors);
        var target = targets.SingleOrDefault(item => item.Number == slot)
            ?? throw new InvalidOperationException($"실행 중인 분할 화면 {slot}번이 없습니다.");
        ct.ThrowIfCancellationRequested();
        SettingsStore.Update(settings => Choose(settings, expectedPhysicalPath, expectedLayout, slot, targets.Count));
        return target;
    }
}
