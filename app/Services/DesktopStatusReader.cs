using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public sealed record DesktopSnapshot(IReadOnlyList<MonitorInfo> Monitors, string DriverStatus, string CompositorStatus,
    int ActiveVirtualCount = 0, string ActiveGroupPath = "", LayoutKind ActiveGroupLayout = LayoutKind.TwoVertical,
    IReadOnlyList<SplitTarget>? ActiveTargets = null, int SelectedSlot = 0, string RoutingIssue = "");

/// <summary>Read-only native queries. Call off the UI thread; publish one consistent snapshot.</summary>
public static class DesktopStatusReader
{
    public static DesktopSnapshot Read()
    {
        var monitors = DisplayConfig.Enumerate();
        var settings = SettingsStore.Load();
        IReadOnlyList<SplitTarget> targets = Array.Empty<SplitTarget>();
        string routingIssue = "";
        int virtualCount = monitors.Count(monitor => monitor.IsDeskSplitVirtual);
        if (virtualCount > 0)
        {
            try { targets = SplitPrimarySelector.ReadTargets(monitors); }
            catch (Exception error) { routingIssue = "현재 배치 확인 실패: " + error.Message; }
        }
        int selectedSlot = settings is null ? 0 : SplitPrimarySelector.ReadChoice(settings, settings.PhysicalMonitorPath, settings.Layout, virtualCount);
        return new(monitors.Where(monitor => !monitor.IsDeskSplitVirtual).ToArray(), ReadDriverStatus(), ReadCompositorStatus(), virtualCount,
            settings?.PhysicalMonitorPath ?? "", settings?.Layout ?? LayoutKind.TwoVertical, targets, selectedSlot, routingIssue);
    }

    public static string ReadDriverStatus()
    {
        try
        {
            if (!DriverClient.TryFindDevicePath(out string path))
                return "드라이버 미설치 · 레이아웃 편집은 가능하며, 실제 분할에는 드라이버를 별도로 설치해야 합니다.";
            try {
                var status = DriverClient.GetStatus();
                return $"드라이버 연결됨 · 통신 v{status.Version} · 가상 모니터 {status.ActiveMonitorCount}개";
            } catch (DriverIoException) { return $"드라이버 장치는 있지만 상태를 읽지 못했습니다. 장치: {path}"; }
        }
        catch (Exception error) { return "드라이버 확인 실패: " + error.Message; }
    }

    public static string ReadCompositorStatus()
    {
        try {
            if (CompositorLauncher.IsRunning()) return "화면 합성기: 실행 중";
            return CompositorLauncher.TryFindExecutable(out _)
                ? "화면 합성기: 준비됨 · 현재 중지"
                : "화면 합성기 없음 · dscomp.exe를 빌드하거나 설치 파일을 확인해 주세요.";
        }
        catch (Exception error) { return "화면 합성기 상태 확인 실패: " + error.Message; }
    }
}
