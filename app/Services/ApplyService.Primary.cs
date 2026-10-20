using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public static partial class ApplyService
{
    // ------------------------------------------------------------------ primary display

    private static bool SameDevice(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
           string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static bool SameMonitorTarget(MonitorInfo before, MonitorInfo current)
        => !string.IsNullOrWhiteSpace(before.MonitorDevicePath)
            ? SameDevice(before.MonitorDevicePath, current.MonitorDevicePath)
            : before.AdapterIdLow == current.AdapterIdLow && before.AdapterIdHigh == current.AdapterIdHigh && before.TargetId == current.TargetId;

    public static string WindowsPrimaryForSplit(MonitorInfo physical, IReadOnlyList<MonitorInfo> current, IReadOnlyList<MonitorInfo> virtuals)
    {
        var primary = current.SingleOrDefault(monitor => monitor.IsPrimary);
        return primary is not null && !primary.IsDeskSplitVirtual && !SameMonitorTarget(physical, primary)
            ? primary.DeviceName : virtuals[0].DeviceName;
    }

    /// <summary>
    /// Determines which display should be restored as primary on Revert.
    /// If a split is already active (one of our virtual monitors currently holds primary),
    /// the value saved by the first Apply is carried forward rather than overwritten —
    /// otherwise a second Apply would "remember" a virtual monitor as the original primary.
    /// </summary>
    private static (string Device, string FriendlyName) CapturePreviousPrimary(
        IReadOnlyList<MonitorInfo> before, IProgress<string> log)
    {
        MonitorInfo? livePrimary = before.FirstOrDefault(m => m.IsPrimary);

        if (livePrimary is not null && !livePrimary.IsDeskSplitVirtual)
        {
            log.Report($"현재 주 모니터: {livePrimary.DisplayLabel} ({livePrimary.DeviceName}) " +
                       "· 복원 시 이 모니터를 주 모니터로 되돌립니다.");
            return (livePrimary.DeviceName, livePrimary.FriendlyName);
        }

        AppSettings? prior = SettingsStore.Load();
        if (prior is not null && !string.IsNullOrEmpty(prior.PreviousPrimaryDevice))
        {
            log.Report("A split is already active; carrying forward the saved pre-split primary " +
                       $"{prior.PreviousPrimaryDevice}.");
            return (prior.PreviousPrimaryDevice, prior.PreviousPrimaryFriendlyName);
        }

        MonitorInfo? fallback = before.FirstOrDefault(m => !m.IsDeskSplitVirtual);
        if (fallback is null)
        {
            log.Report("WARNING: could not determine a physical display to restore as primary on Revert.");
            return (string.Empty, string.Empty);
        }

        log.Report($"No saved pre-split primary; will restore {fallback.DeviceName} on Revert.");
        return (fallback.DeviceName, fallback.FriendlyName);
    }

    /// <summary>
    /// Serialises every display-mutating operation: Apply, Revert and the automatic
    /// suspend/resume primary switches. Without this an automatic suspend could land in the
    /// middle of an Apply and leave the desktop half-rearranged.
    ///
    /// Released for UI/helper waits. UserOperationGate still owns the entire transaction,
    /// including confirmation and compensation; only secure-desktop reconciliation can enter.
    /// </summary>
    public static readonly OperationGate DisplayOperationGate = new();
    public static readonly OperationGate UserOperationGate = new();

    /// <summary>
    /// Makes <paramref name="targetDevice"/> primary. Public so the suspend/resume watcher can
    /// hand primary back and forth without duplicating the re-basing logic.
    /// </summary>
    public static bool SwitchPrimaryTo(string targetDevice, string description, IProgress<string> log)
        => SwitchPrimary(targetDevice, description, log);

    private static bool SwitchPrimary(string targetDevice, string description, IProgress<string> log)
    {
        if (string.IsNullOrEmpty(targetDevice))
        {
            log.Report("WARNING: no target display for the primary switch; leaving it unchanged.");
            return false;
        }

        IReadOnlyList<MonitorInfo> current = ReadMonitors();
        if (current.Count == 0)
        {
            log.Report("WARNING: could not enumerate displays; primary display left unchanged.");
            return false;
        }

        MonitorInfo? livePrimary = current.FirstOrDefault(m => m.IsPrimary);
        if (livePrimary is not null && SameDevice(livePrimary.DeviceName, targetDevice))
        {
            log.Report($"{description} is already the primary display.");
            return true;
        }

        IReadOnlyList<DisplayArranger.MonitorPlacement> snapshot = DisplayArranger.SnapshotLayout(current);
        DisplayArranger.MonitorPlacement? origin =
            snapshot.FirstOrDefault(p => SameDevice(p.DeviceName, targetDevice));
        if (origin is null)
        {
            log.Report($"WARNING: {targetDevice} is not in the current display set; primary left unchanged.");
            return false;
        }

        try
        {
            IReadOnlyList<DisplayArranger.MonitorPlacement> rebased =
                DisplayArranger.RebaseToPrimary(snapshot, targetDevice);

            log.Report($"Making {description} the primary display — re-basing all " +
                       $"{rebased.Count} monitor(s) by ({-origin.X},{-origin.Y}):");
            DisplayArranger.ApplyLayout(rebased, log.Report);
            return true;
        }
        catch (DisplayArrangeException ex)
        {
            log.Report("WARNING: primary switch failed — " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Picks the best physical monitor to hold primary once the split is torn down and
    /// switches to it. Preference order: the display that was primary before Apply, then the
    /// monitor that was split, then whatever physical monitor is available.
    /// </summary>
    private static void RestorePrimaryBeforeTeardown(IProgress<string> log)
    {
        var physicalMonitors = ReadMonitors().Where(m => !m.IsDeskSplitVirtual).ToList();
        if (physicalMonitors.Count == 0)
        {
            log.Report("WARNING: no physical monitors found; leaving the primary display alone.");
            return;
        }

        AppSettings? settings = SettingsStore.Load();
        MonitorInfo? target = null;
        string why = string.Empty;

        if (settings is not null)
        {
            target = physicalMonitors.FirstOrDefault(m => SameDevice(m.DeviceName, settings.PreviousPrimaryDevice));
            if (target is not null) why = "primary before the split";

            if (target is null && !string.IsNullOrWhiteSpace(settings.PreviousPrimaryFriendlyName))
            {
                target = physicalMonitors.FirstOrDefault(
                    m => SameDevice(m.FriendlyName, settings.PreviousPrimaryFriendlyName));
                if (target is not null) why = "primary before the split, matched by name";
            }

            if (target is null)
            {
                target = physicalMonitors.FirstOrDefault(m => SameDevice(m.DeviceName, settings.PhysicalDevice));
                if (target is not null) why = "the monitor that was split; no saved previous primary";
            }
        }

        if (target is null)
        {
            target = physicalMonitors.FirstOrDefault(m => m.IsPrimary) ?? physicalMonitors[0];
            why = "first available physical monitor; no usable saved state";
        }

        SwitchPrimary(target.DeviceName, $"{target.DisplayLabel} ({target.DeviceName}) [{why}]", log);
    }

}
