using DesktopSplitter.Interop;
using DesktopSplitter.Models;
using Microsoft.Win32;

namespace DesktopSplitter.Services;

/// <summary>State of a status row.</summary>
public enum CheckState
{
    /// <summary>Could not be determined.</summary>
    Unknown,

    /// <summary>A capability or setting that is satisfied.</summary>
    Ok,

    /// <summary>A capability or setting that is NOT satisfied.</summary>
    No,

    /// <summary>
    /// Purely informational — neither good nor bad. Used for "Currently hidden: no", which is the
    /// normal resting state and must not read as a problem.
    /// </summary>
    Info,
}

public sealed record StatusRow(string Label, CheckState State, string Detail);

/// <summary>Everything the display-hiding panel shows, gathered in one pass.</summary>
public sealed record HidingSnapshot(
    StatusRow SystemSupport,
    StatusRow MonitorSupport,
    StatusRow CurrentlySpecialized,
    StatusRow Intent,
    bool SplitActive,
    bool Available,
    bool EnabledNow,
    bool IntentOn)
{
    public IReadOnlyList<StatusRow> Rows => new[] { SystemSupport, MonitorSupport, CurrentlySpecialized, Intent };
}

/// <summary>
/// Read-only probes behind the "Display hiding" panel, built on DisplayConfig monitor
/// specialization (GET, type 12) — the supported mechanism that Settings &gt; Advanced display
/// uses. Nothing here changes display state.
/// </summary>
public static class HidingStatus
{
    private const string DeskSplitKey = @"SOFTWARE\DesktopSplitter";
    private const string HideFlagValue = "HidePhysicalDisplay";

    public static HidingSnapshot Query(MonitorInfo? selected)
    {
        bool intentOn = ReadIntent();
        StatusRow intent = intentOn
            ? new StatusRow("다음 적용 시 숨김", CheckState.Ok,
                "예약됨 · 분할 적용이 완료된 뒤 모니터를 숨깁니다. 지금 숨겨진 상태라는 뜻은 아닙니다.")
            : new StatusRow("다음 적용 시 숨김", CheckState.No,
                "예약 안 함 · 기본 방식으로 화면을 합성합니다.");

        if (selected is null) return Degenerate("선택된 모니터가 없습니다.", intent, intentOn);

        if (!MonitorSpecialization.TryGet(selected, out SpecializationState state, out int rc))
        {
            string detail = $"숨김 상태 조회 실패 (오류 {rc})" +
                            (rc == MonitorSpecialization.ERROR_INVALID_PARAMETER
                                ? " · 현재 Windows에서는 숨김 상태 조회가 지원되지 않을 수 있습니다."
                                : ".");
            return Degenerate(detail, intent, intentOn);
        }

        // Capability rows say what is POSSIBLE. The state row says what is HAPPENING. Keeping the
        // two visibly distinct matters: a green "supports hiding" was previously misread as
        // "already hiding".
        var system = new StatusRow("Windows 지원 여부",
            state.AvailableForSystem ? CheckState.Ok : CheckState.No,
            state.AvailableForSystem
                ? "지원함 · 현재 숨김 여부와는 별개입니다."
                : "지원하지 않음 · 일반 분할은 숨김 기능 없이 사용할 수 있습니다.");

        var monitor = new StatusRow("선택 모니터 지원 여부",
            state.AvailableForMonitor ? CheckState.Ok : CheckState.No,
            state.AvailableForMonitor
                ? $"지원함 · {selected.DisplayLabel} (지원 정보 0x{state.RawValue:X})"
                : $"지원하지 않음 · {selected.DisplayLabel} (지원 정보 0x{state.RawValue:X})");

        var current = new StatusRow("현재 실제 숨김 상태",
            state.EnabledNow ? CheckState.Ok : CheckState.Info,
            state.EnabledNow
                ? $"숨겨져 있음 · {selected.DisplayLabel}이 현재 Windows 바탕 화면에서 제외되어 있습니다."
                : $"숨겨져 있지 않음 · {selected.DisplayLabel}은 현재 일반 모니터로 표시됩니다.");

        return new HidingSnapshot(system, monitor, current, intent,
                                  IsSplitActive(), state.Available, state.EnabledNow, intentOn);
    }

    private static HidingSnapshot Degenerate(string detail, StatusRow intent, bool intentOn)
        => new(new StatusRow("Windows 지원 여부", CheckState.Unknown, detail),
               new StatusRow("선택 모니터 지원 여부", CheckState.Unknown, detail),
               new StatusRow("현재 실제 숨김 상태", CheckState.Unknown, detail),
               intent, IsSplitActive(), false, false, intentOn);

    /// <summary>A split must be reverted before hiding can be enabled or disabled.</summary>
    public static bool IsSplitActive()
    {
        try
        {
            if (DisplayConfig.Enumerate().Any(m => m.IsDeskSplitVirtual)) return true;
        }
        catch (DisplayConfigException)
        {
            // Fall through to the compositor check.
        }
        return CompositorLauncher.IsRunning();
    }

    // ------------------------------------------------------------------ intent flag

    /// <summary>
    /// The user's saved choice is authoritative. HKLM is only a legacy default when no user file exists.
    /// </summary>
    public static bool ReadIntent()
    {
        var user = SettingsStore.Load();
        return ResolveIntent(user, () => ReadDword(Registry.LocalMachine, DeskSplitKey, HideFlagValue));
    }

    public static bool ResolveIntent(AppSettings? user, Func<int?> readMachineDefault)
        => user?.HidePhysicalDisplay ?? (readMachineDefault() is int value && value != 0);

    public sealed record IntentWriteResult(bool Success, bool MachineWideWritten, string Message);

    /// <summary>
    /// Records the intent. The per-user copy always succeeds; the HKLM mirror is best-effort and
    /// its failure is reported, never worked around by elevating.
    /// </summary>
    public static IntentWriteResult WriteIntent(bool enabled)
    {
        try
        {
            SettingsStore.Update(settings => settings.HidePhysicalDisplay = enabled);
        }
        catch (Exception ex)
        {
            return new IntentWriteResult(false, false, "설정을 저장하지 못했습니다: " + ex.Message);
        }

        try
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(DeskSplitKey, writable: true)
                                   ?? throw new UnauthorizedAccessException("key could not be opened");
            key.SetValue(HideFlagValue, enabled ? 1 : 0, RegistryValueKind.DWord);
            return new IntentWriteResult(true, true,
                $"다음 적용 시 숨김 예약을 {(enabled ? "설정" : "해제")}했습니다. 사용자 및 시스템 설정에 저장했으며, 현재 디스플레이는 변경하지 않았습니다.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new IntentWriteResult(true, false,
                $"다음 적용 시 숨김 예약을 {(enabled ? "설정" : "해제")}했습니다. " +
                "사용자 설정에 저장했습니다. 관리자 권한이 필요한 시스템 설정은 변경하지 않았으며, 현재 디스플레이도 변경하지 않았습니다.");
        }
        catch (Exception ex)
        {
            return new IntentWriteResult(true, false,
                $"사용자 숨김 예약을 {(enabled ? "설정" : "해제")}했습니다. 시스템 설정 저장 실패: {ex.Message}");
        }
    }

    private static int? ReadDword(RegistryKey root, string subKey, string name)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(subKey);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }
}
