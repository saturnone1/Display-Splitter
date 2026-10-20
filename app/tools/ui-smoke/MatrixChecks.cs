using System.Text.Json;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;
using DesktopSplitter.Services;

internal static class MatrixChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run()
    {
        var settings = new AppSettings { PhysicalMonitorPath = "panel-A", Layout = LayoutKind.Grid2x2, AppliedPrimaryDevice = "OTHER-WINDOWS-PRIMARY", PreviousPrimaryDevice = "OTHER-WINDOWS-PRIMARY" };
        settings.MatrixPrimarySlots[SplitPrimarySelector.ProfileKey("panel-B", LayoutKind.Grid2x2)] = 4;
        SplitPrimarySelector.Choose(settings, "PANEL-a", LayoutKind.Grid2x2, 3, 4);
        Check(settings.AppliedPrimaryDevice == "OTHER-WINDOWS-PRIMARY" && settings.PreviousPrimaryDevice == "OTHER-WINDOWS-PRIMARY", "Matrix choice changed Windows primary metadata");
        Check(SplitPrimarySelector.ReadChoice(settings, "panel-B", LayoutKind.Grid2x2, 4) == 4 && SplitPrimarySelector.ReadChoice(settings, "panel-A", LayoutKind.Grid2x2, 4) == 3, "Group choice leaked to another monitor");
        try { SplitPrimarySelector.Choose(settings, "panel-B", LayoutKind.Grid2x2, 1, 4); throw new Exception("Foreign group was accepted"); } catch (InvalidOperationException) { }
        var physical = new MonitorInfo { DeviceName = "P", MonitorDevicePath = "panel-A" };
        var other = new MonitorInfo { DeviceName = "OTHER", MonitorDevicePath = "panel-B", IsPrimary = true };
        var virtuals = new[] { new MonitorInfo { DeviceName = "V1", IsDeskSplitVirtual = true } };
        Check(ApplyService.WindowsPrimaryForSplit(physical, new[] { physical, other }, virtuals) == "OTHER", "Split stole another physical monitor's primary role");

        var system = new FakeWindows();
        var engine = new MatrixWorkspace(system);
        var area = new MonitorArea("V", new(-1280, 0, 1280, 1024), new(-1240, 0, 1240, 1024));
        MatrixCandidate Candidate(long handle, string device) => new(new(handle, checked((int)handle), 12345, "fixture", "", 0), device, new(100, 100, 800, 600), area with { DeviceName = device }, handle % 2 == 0);
        system.Candidates.Add(Candidate(999, "OTHER")); // The foreground app is on a different monitor.
        for (int i = 1; i <= 4; i++) system.Candidates.Add(Candidate(100 + i, $"V{i}"));
        system.Candidates.Add(Candidate(888, "V1")); // Behind V1's representative: must not be selected.
        var targets = Enumerable.Range(1, 4).Select(slot => new SplitTarget(slot, $"V{slot}", area.Bounds, area.Work)).ToArray();
        var memory = engine.Capture("panel-A", LayoutKind.Grid2x2, 3, targets);
        Check(memory.Windows.Select(window => window.Identity.Handle).SequenceEqual(new long[] { 101, 102, 103, 104 }), "Capture used another monitor or a background window");
        Check(MatrixWorkspace.IsValid(memory), "Valid capture rejected");
        var plan = engine.Prepare(memory, targets.Select(target => target.DeviceName));
        var merged = new MatrixDestination("panel-A", LayoutKind.Grid2x2, true, targets.Select(target => target with { DeviceName = "P" }).ToArray());
        var result = engine.Restore(plan, merged, 3, default);
        Check(result.Placed == 4 && result.Focused && system.Operations[^1] == "front:103" && system.Operations.All(operation => !operation.Contains("999")), "Merged representatives/focus escaped their group");
        Check(system.Placements.All(placement => placement.DeviceName == "P"), "Merged windows went to a different physical monitor");
        system.ClearOperations();
        try { engine.Restore(plan, merged with { PhysicalMonitorPath = "panel-B" }, 3, default); throw new Exception("Cross-monitor restore accepted"); } catch (InvalidOperationException) { }
        Check(system.Operations.Count == 0, "Foreign destination moved a window before rejection");

        // User moved representative 2 to another real monitor while merged; a new split must leave it there.
        system.Candidates[2] = system.Candidates[2] with { DeviceName = "OTHER" };
        var prepared = engine.Prepare(memory, targets.Select(target => target.DeviceName));
        // Then Windows may rebase/move all windows while applying the split; eligibility stays bound to the pre-change group.
        var split = new MatrixDestination("panel-A", LayoutKind.Grid2x2, false, targets);
        result = engine.Restore(prepared, split, 3, default);
        Check(result.Placed == 3 && result.Skipped == 1 && system.Placements.All(placement => placement.Handle != 102), "Re-split stole a window manually moved to another monitor");
        system.ClearOperations();
        system.Live.Remove(103); // Closed representative; no alternate app may replace it.
        system.Live[104] = system.Live[104] with { MarkerValue = 9999 }; // Recycled HWND/process/class, new lifetime.
        result = engine.Restore(plan, split, 3, default);
        Check(result.Placed == 2 && result.Skipped == 2 && !result.Focused && system.Operations.All(operation => operation != "front:101"), "Closed/reused main window was replaced or focused another monitor");
        system.ClearOperations();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { engine.Restore(plan, split, 3, cancel.Token); throw new Exception("Cancelled restore succeeded"); } catch (OperationCanceledException) { }
        Check(system.Operations.Count == 0, "Cancelled restore moved windows");
        var duplicate = JsonSerializer.Deserialize<MatrixSnapshot>(JsonSerializer.Serialize(memory))!;
        duplicate.Windows.Add(duplicate.Windows[0]);
        Check(!MatrixWorkspace.IsValid(duplicate), "Duplicate identity accepted");
        var roundTrip = JsonSerializer.Deserialize<MatrixSnapshot>(JsonSerializer.Serialize(memory))!;
        Check(MatrixWorkspace.IsValid(roundTrip) && roundTrip.Windows[0] == memory.Windows[0], "Matrix persistence lost placement/identity information");
        Check(!MatrixWorkspace.Matches(memory, "panel-A", LayoutKind.FourColumns, 4), "Different layout reused wrong matrix");
        // Every possible four-slot routing: capture current positions, merge, re-split using fresh device IDs.
        var config = new CompositorConfig { Segments = targets.Select(target => new CompositorSegment { VirtualDevice = target.DeviceName }).ToList() };
        int cases = 0;
        foreach (int a in Enumerable.Range(0, 4))
        foreach (int b in Enumerable.Range(0, 4))
        foreach (int c in Enumerable.Range(0, 4))
        foreach (int d in Enumerable.Range(0, 4))
        {
            int[] order = [a, b, c, d];
            if (order.Distinct().Count() != 4) continue;
            var swappedSystem = new FakeWindows();
            for (int i = 1; i <= 4; i++) swappedSystem.Candidates.Add(Candidate(100 + i, $"V{i}"));
            var swappedEngine = new MatrixWorkspace(swappedSystem);
            var mapped = SplitDisplayReorder.MapTargets(targets, config, order);
            var captured = swappedEngine.Capture("panel-A", LayoutKind.Grid2x2, 3, mapped);
            Check(captured.Windows.OrderBy(window => window.Slot).Select(window => window.Identity.Handle)
                .SequenceEqual(order.Select(source => 101L + source)), "Swapped content was recorded under an old device number");
            var beforeMerge = swappedEngine.Prepare(captured, mapped.Select(target => target.DeviceName));
            swappedEngine.Restore(beforeMerge, merged, 3, default);
            Check(swappedSystem.Operations[^1] == $"front:{101 + order[2]}", "Selection followed a device instead of fixed slot 3");
            swappedSystem.ClearOperations();
            for (int i = 0; i < 4; i++) swappedSystem.Candidates[i] = swappedSystem.Candidates[i] with { DeviceName = "P" };
            var afterMerge = swappedEngine.Prepare(captured, new[] { "P" });
            var freshTargets = targets.Select(target => target with { DeviceName = $"NEW{target.Number}" }).ToArray();
            swappedEngine.Restore(afterMerge, new("panel-A", LayoutKind.Grid2x2, false, freshTargets), 3, default);
            Check(swappedSystem.Placements.All(placement => placement.DeviceName == $"NEW{Array.IndexOf(order, (int)placement.Handle - 101) + 1}"),
                "Re-split reused old device names or lost the captured slot mapping");
            cases++;
        }
        Check(cases == 24, "Routing permutations incomplete");
        settings.MatrixMemories.Add(memory);
        var foreignMemory = new MatrixSnapshot { PhysicalMonitorPath = "panel-B", Layout = LayoutKind.Grid2x2 };
        settings.MatrixMemories.Add(foreignMemory);
        Check(SplitMatrixWorkspace.DiscardUnverifiedMemory(settings, "PANEL-A", LayoutKind.Grid2x2).Count == 1
            && settings.MatrixMemories.SequenceEqual(new[] { foreignMemory }), "Failed capture reused stale memory or erased another group");
        try { engine.Capture("panel-A", LayoutKind.Grid2x2, 3, targets.Select(target => target with { DeviceName = "V1" }).ToArray());
            throw new Exception("Duplicate capture devices accepted"); } catch (InvalidOperationException) { }
        Console.WriteLine("PASS matrix: per-monitor/layout representative choice, Windows-primary isolation, four-window merge/re-split, front ordering, foreign monitor exclusion, manual move exclusion, closed/recycled handles, cancellation and persistence; fake window adapter only");
    }

    private sealed class FakeWindows : IMatrixWindows
    {
        public List<MatrixCandidate> Candidates { get; } = new();
        public Dictionary<long, MatrixWindowIdentity> Live { get; } = new();
        public List<string> Operations { get; } = new();
        public List<(long Handle, string DeviceName)> Placements { get; } = new();
        public IReadOnlyList<MatrixCandidate> ReadFrontToBack() => Candidates;
        public MatrixWindowIdentity? Remember(MatrixCandidate window, string name) {
            var identity = window.Identity with { MarkerName = name, MarkerValue = window.Identity.Handle };
            Live[identity.Handle] = identity; return identity;
        }
        public bool IsCurrent(MatrixWindowIdentity window) => Live.GetValueOrDefault(window.Handle) == window;
        public bool Place(MatrixWindow window, SplitTarget target) {
            Operations.Add($"place:{window.Identity.Handle}:{target.Number}"); Placements.Add((window.Identity.Handle, target.DeviceName)); return true;
        }
        public bool BringToFront(MatrixWindowIdentity window) { Operations.Add($"front:{window.Handle}"); return true; }
        public void Forget(MatrixWindowIdentity window) { if (IsCurrent(window)) Live.Remove(window.Handle); }
        public void ClearOperations() { Operations.Clear(); Placements.Clear(); }
    }
}
