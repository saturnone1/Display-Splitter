using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public sealed record DesktopSnapshot(IReadOnlyList<MonitorInfo> Monitors, string DriverStatus, string CompositorStatus,
    int ActiveVirtualCount = 0, string ActiveGroupPath = "", LayoutKind ActiveGroupLayout = LayoutKind.TwoVertical,
    IReadOnlyList<SplitTarget>? ActiveTargets = null, int SelectedSlot = 0, string RoutingIssue = "",
    IReadOnlyList<LayoutCut>? ActiveCustomCuts = null);

/// <summary>Read-only native queries. Call off the UI thread; publish one consistent snapshot.</summary>
public static class DesktopStatusReader
{
    public static DesktopSnapshot Read(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var monitors = DisplayConfig.Enumerate();
        ct.ThrowIfCancellationRequested();
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
        return new(monitors.Where(monitor => !monitor.IsDeskSplitVirtual).ToArray(), ReadDriverStatus(ct), ReadCompositorStatus(), virtualCount,
            settings?.PhysicalMonitorPath ?? "", settings?.Layout ?? LayoutKind.TwoVertical, targets, selectedSlot, routingIssue, settings?.CustomLayoutCuts);
    }

    public static string ReadDriverStatus(CancellationToken ct = default)
    {
        try
        {
            using var connection = DriverClient.Connect(ct);
            var diagnostic = DriverClient.ReadDiagnostics(connection.Handle);
            if (DriverClient.AdapterFailureMessage(diagnostic) is { } failure) return failure;
            if (diagnostic is { AdapterState: 0 or 1 }) return "드라이버 연결됨 · 가상 디스플레이 어댑터 초기화 중";
            return $"드라이버 연결됨 · 통신 v{connection.Status.Version} · 가상 모니터 {connection.Status.ActiveMonitorCount}개";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DriverNotFoundException error) { return error.Message; }
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
