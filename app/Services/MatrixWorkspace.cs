using System.Security.Cryptography;
using System.Text;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public sealed record MatrixCandidate(MatrixWindowIdentity Identity, string DeviceName, WindowBox Normal, MonitorArea Source, bool Maximized);
public sealed record MatrixRestorePlan(MatrixSnapshot Memory, IReadOnlySet<MatrixWindowIdentity> Eligible);
public sealed record MatrixDestination(string PhysicalMonitorPath, LayoutKind Layout, bool Merged, IReadOnlyList<SplitTarget> Targets);
public interface IMatrixWindows
{
    IReadOnlyList<MatrixCandidate> ReadFrontToBack();
    MatrixWindowIdentity? Remember(MatrixCandidate window, string markerName);
    bool IsCurrent(MatrixWindowIdentity window);
    bool Place(MatrixWindow window, SplitTarget target);
    bool BringToFront(MatrixWindowIdentity window);
    void Forget(MatrixWindowIdentity window);
}

/// <summary>Pure orchestration; injected window operations keep all regression tests off the desktop.</summary>
public sealed class MatrixWorkspace(IMatrixWindows windows)
{
    public static string MarkerNameFor(string physicalPath, LayoutKind layout)
        => "DesktopSplitter.Matrix." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(physicalPath.ToUpperInvariant() + ":" + layout)))[..24];
    public static bool Matches(MatrixSnapshot memory, string physicalPath, LayoutKind layout, int count)
        => !string.IsNullOrWhiteSpace(physicalPath) && string.Equals(memory.PhysicalMonitorPath, physicalPath, StringComparison.OrdinalIgnoreCase)
            && memory.Layout == layout && memory.SlotCount == count;

    public static bool IsValid(MatrixSnapshot memory)
        => !string.IsNullOrWhiteSpace(memory.PhysicalMonitorPath) && Enum.IsDefined(memory.Layout)
            && memory.SlotCount is >= 1 and <= 4 && memory.SlotCount == LayoutDefinition.FromKind(memory.Layout).SegmentCount
            && memory.PrimarySlot >= 1 && memory.PrimarySlot <= memory.SlotCount
            && memory.Windows is not null && memory.Windows.Count <= memory.SlotCount
            && memory.Windows.All(window => window is not null && window.Slot >= 1 && window.Slot <= memory.SlotCount
                && window.Identity is { Handle: > 0, ProcessId: > 0, ProcessStartedUtcTicks: > 0, MarkerValue: > 0 }
                && window.Identity.MarkerName == MarkerNameFor(memory.PhysicalMonitorPath, memory.Layout)
                && window.NormalBounds.Width > 0 && window.NormalBounds.Height > 0
                && window.SourceBounds.Width > 0 && window.SourceBounds.Height > 0 && window.SourceWork.Width > 0 && window.SourceWork.Height > 0)
            && memory.Windows.Select(window => window.Slot).Distinct().Count() == memory.Windows.Count
            && memory.Windows.Select(window => window.Identity.Handle).Distinct().Count() == memory.Windows.Count;

    public MatrixSnapshot Capture(string physicalPath, LayoutKind layout, int primary, IReadOnlyList<SplitTarget> targets)
    {
        if (string.IsNullOrWhiteSpace(physicalPath) || targets.Count is < 1 or > 4 || primary < 1 || primary > targets.Count
            || !Enum.IsDefined(layout) || targets.Count != LayoutDefinition.FromKind(layout).SegmentCount
            || !targets.Select(target => target.Number).Order().SequenceEqual(Enumerable.Range(1, targets.Count))
            || targets.Any(target => string.IsNullOrWhiteSpace(target.DeviceName) || target.Bounds.Width <= 0 || target.Bounds.Height <= 0
                || target.Work.Width <= 0 || target.Work.Height <= 0)
            || targets.Select(target => SplitWindowMover.NormalizeDevice(target.DeviceName)).Distinct().Count() != targets.Count)
            throw new InvalidOperationException("매트릭스를 기억할 모니터 또는 분할 칸 정보가 올바르지 않습니다.");
        string marker = MarkerNameFor(physicalPath, layout);
        var candidates = windows.ReadFrontToBack();
        var memory = new MatrixSnapshot { PhysicalMonitorPath = physicalPath, Layout = layout, SlotCount = targets.Count, PrimarySlot = primary };
        foreach (var target in targets.OrderBy(target => target.Number)) {
            // One eligible front window per slot; never borrow from a different slot or monitor.
            var candidate = candidates.FirstOrDefault(window => SplitWindowMover.NormalizeDevice(window.DeviceName) == SplitWindowMover.NormalizeDevice(target.DeviceName));
            if (candidate is null) continue;
            var identity = windows.Remember(candidate, marker);
            if (identity is not null) memory.Windows.Add(new(target.Number, identity, candidate.Normal, candidate.Source.Bounds, candidate.Source.Work, candidate.Maximized));
        }
        return memory;
    }

    public MatrixRestorePlan Prepare(MatrixSnapshot memory, IEnumerable<string> allowedDevices)
    {
        if (!IsValid(memory)) throw new InvalidOperationException("기억한 매트릭스 정보가 올바르지 않습니다.");
        var allowed = allowedDevices.Select(SplitWindowMover.NormalizeDevice).ToHashSet();
        var candidates = windows.ReadFrontToBack();
        var eligible = memory.Windows.Where(window => windows.IsCurrent(window.Identity)
            && candidates.Any(candidate => candidate.Identity.Handle == window.Identity.Handle && allowed.Contains(SplitWindowMover.NormalizeDevice(candidate.DeviceName))))
            .Select(window => window.Identity).ToHashSet();
        return new(memory, eligible);
    }

    public (int Placed, int Skipped, bool Focused) Restore(MatrixRestorePlan plan, MatrixDestination destination, int primarySlot, CancellationToken ct)
    {
        var memory = plan.Memory;
        var targets = destination.Targets;
        if (!IsValid(memory) || !Matches(memory, destination.PhysicalMonitorPath, destination.Layout, targets.Count)
            || primarySlot < 1 || primarySlot > memory.SlotCount
            || !targets.Select(target => target.Number).Order().SequenceEqual(Enumerable.Range(1, memory.SlotCount))
            || targets.Any(target => string.IsNullOrWhiteSpace(target.DeviceName) || target.Work.Width <= 0 || target.Work.Height <= 0)
            || targets.Select(target => SplitWindowMover.NormalizeDevice(target.DeviceName)).Distinct().Count() != (destination.Merged ? 1 : memory.SlotCount))
            throw new InvalidOperationException("기억한 매트릭스 정보가 올바르지 않아 창을 복원하지 않았습니다.");
        int placed = 0, skipped = 0;
        MatrixWindowIdentity? primary = null;
        foreach (var window in memory.Windows.OrderBy(window => window.Slot == primarySlot ? 1 : 0)) {
            ct.ThrowIfCancellationRequested();
            var target = targets.SingleOrDefault(target => target.Number == window.Slot);
            bool accepted = false;
            try { accepted = plan.Eligible.Contains(window.Identity) && target is not null && windows.IsCurrent(window.Identity) && windows.Place(window, target); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException or OverflowException) { }
            if (!accepted) { skipped++; continue; }
            placed++;
            if (window.Slot == primarySlot) primary = window.Identity;
        }
        ct.ThrowIfCancellationRequested();
        bool focused = primary is not null && windows.IsCurrent(primary) && windows.BringToFront(primary);
        return (placed, skipped, focused);
    }

    public void Release(MatrixSnapshot old, MatrixSnapshot? replacement)
    {
        if (!IsValid(old)) return;
        foreach (var window in old.Windows)
            if (replacement?.Windows.Any(next => next.Identity == window.Identity) != true) windows.Forget(window.Identity);
    }
}
