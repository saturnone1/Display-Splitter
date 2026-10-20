using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>Swaps whole virtual desktops between numbered slots. Only explicit hotkeys enter MoveFocusedTo.</summary>
public static class SplitDisplayReorder
{
    public const uint Signature = 0x44530000;

    public static uint Encode(IReadOnlyList<int> sources)
    {
        if (sources.Count is < 1 or > 4 || !sources.Order().SequenceEqual(Enumerable.Range(0, sources.Count)))
            throw new ArgumentException("잘못된 화면 배치 순서입니다.");
        uint result = Signature | ((uint)sources.Count << 8);
        for (int i = 0; i < sources.Count; i++) result |= (uint)sources[i] << (i * 2);
        return result;
    }

    public static int[] Decode(uint routing)
    {
        int count = (int)((routing >> 8) & 255);
        if (count is < 1 or > 4) throw new InvalidOperationException("합성기가 화면 배치 변경을 지원하지 않습니다. 새 합성기로 분할을 다시 적용해 주세요.");
        var order = Enumerable.Range(0, count).Select(i => (int)((routing >> (i * 2)) & 3)).ToArray();
        if (Encode(order) != routing) throw new InvalidOperationException("합성기의 화면 배치 정보가 올바르지 않습니다.");
        return order;
    }

    public static uint Fingerprint(CompositorConfig config)
    {
        uint hash = 2166136261;
        void Add(string text) { foreach (char c in text) hash = unchecked((hash ^ c) * 16777619); }
        Add(config.PhysicalDevice + "|");
        foreach (var s in config.Segments)
            Add(string.Create(CultureInfo.InvariantCulture, $"{s.VirtualDevice},{s.Width},{s.Height},{s.PhysRect.X},{s.PhysRect.Y},{s.PhysRect.W},{s.PhysRect.H};"));
        return hash;
    }

    public static int[] Swap(IReadOnlyList<int> order, int sourceIndex, int slot)
    {
        _ = Encode(order);
        if (slot < 1 || slot > order.Count || sourceIndex < 0 || sourceIndex >= order.Count)
            throw new InvalidOperationException("해당 분할 칸이 없습니다.");
        var result = order.ToArray();
        int oldSlot = Array.IndexOf(result, sourceIndex);
        (result[oldSlot], result[slot - 1]) = (result[slot - 1], result[oldSlot]);
        return result;
    }

    public static IReadOnlyList<DisplayArranger.MonitorPlacement> Plan(
        IReadOnlyList<MonitorInfo> monitors, CompositorConfig config, IReadOnlyList<int> order)
    {
        _ = Encode(order);
        if (config.Segments.Count != order.Count) throw new InvalidOperationException("분할 칸 수가 바뀌었습니다.");
        var virtuals = config.Segments.Select(s => monitors.SingleOrDefault(m =>
            SplitWindowMover.NormalizeDevice(m.DeviceName) == SplitWindowMover.NormalizeDevice(s.VirtualDevice) && m.IsDeskSplitVirtual)
            ?? throw new InvalidOperationException("실행 중인 분할 화면을 찾지 못했습니다.")).ToArray();
        if (virtuals.Select(m => m.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != order.Count)
            throw new InvalidOperationException("중복된 화면 배치 정보입니다.");
        // Keep each source's native mode. Recompute grid tracks from the fixed physical
        // slot geometry so unequal resolutions/spanned cells cannot overlap.
        var rects = config.Segments.Select(s => s.PhysRect).ToArray();
        if (rects.Any(r => r.W <= 0 || r.H <= 0 || r.X < 0 || r.Y < 0)) throw new InvalidOperationException("분할 칸의 크기가 올바르지 않습니다.");
        var xs = rects.SelectMany(r => new[] { r.X, checked(r.X + r.W) }).Distinct().Order().ToArray();
        var ys = rects.SelectMany(r => new[] { r.Y, checked(r.Y + r.H) }).Distinct().Order().ToArray();
        var widths = new int[xs.Length - 1]; var heights = new int[ys.Length - 1];
        for (int pass = 0; pass < 2; pass++)
        for (int i = 0; i < order.Count; i++)
        {
            var r = rects[i]; var m = virtuals[order[i]];
            int left = Array.IndexOf(xs, r.X), right = Array.IndexOf(xs, r.X + r.W);
            int top = Array.IndexOf(ys, r.Y), bottom = Array.IndexOf(ys, r.Y + r.H);
            if (m.CurrentWidth <= 0 || m.CurrentHeight <= 0) throw new InvalidOperationException("실행 중인 화면의 해상도를 읽지 못했습니다.");
            static void Fit(int[] tracks, int start, int end, int size, int pass)
            {
                if (pass == 0 && end - start == 1) tracks[start] = Math.Max(tracks[start], size);
                if (pass == 1 && end - start > 1)
                {
                    int current = tracks.Skip(start).Take(end - start).Sum();
                    if (current < size) tracks[end - 1] = checked(tracks[end - 1] + size - current);
                }
            }
            Fit(widths, left, right, m.CurrentWidth, pass);
            Fit(heights, top, bottom, m.CurrentHeight, pass);
        }
        for (int i = 0; i < widths.Length; i++) widths[i] = Math.Max(1, widths[i]);
        for (int i = 0; i < heights.Length; i++) heights[i] = Math.Max(1, heights[i]);
        int baseX = virtuals.Min(m => m.PositionX), baseY = virtuals.Min(m => m.PositionY);
        var placements = DisplayArranger.SnapshotLayout(monitors).ToDictionary(p => p.DeviceName, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < order.Count; i++)
        {
            var m = virtuals[order[i]]; var r = rects[i];
            placements[m.DeviceName] = placements[m.DeviceName] with {
                X = checked(baseX + widths.Take(Array.IndexOf(xs, r.X)).Sum()),
                Y = checked(baseY + heights.Take(Array.IndexOf(ys, r.Y)).Sum()) };
        }
        var primary = monitors.Single(m => m.IsPrimary);
        var result = DisplayArranger.RebaseToPrimary(placements.Values.ToArray(), primary.DeviceName);
        // Other physical monitors stay put relative to the desktop. Reject a proposed
        // layout that would cover one rather than silently moving it out of the way.
        for (int i = 0; i < result.Count; i++)
            for (int j = i + 1; j < result.Count; j++)
                if (result[i].X < result[j].Right && result[j].X < result[i].Right && result[i].Y < result[j].Bottom && result[j].Y < result[i].Bottom)
                    throw new InvalidOperationException("변경할 배치가 다른 모니터와 겹쳐 취소했습니다. 앱에서 분할 크기를 먼저 조정해 주세요.");
        return result;
    }

    public static void Execute(Action applyPositions, Action commitRouting, Action rollbackPositions, Action rollbackRouting)
    {
        applyPositions(); // A rejected CCD batch has not committed anything to undo.
        try { commitRouting(); }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            try { rollbackRouting(); }
            catch (Exception ex)
            {
                failures.Add(ex);
                throw new AggregateException("합성기 상태를 확인할 수 없어 좌표만 임의로 복원하지 않았습니다. 현재 배치를 확인해 주세요.", failures);
            }
            try { rollbackPositions(); } catch (Exception ex) { failures.Add(ex); }
            if (failures.Count > 1) throw new AggregateException("배치 변경 및 원상 복원에 실패했습니다. 앱에서 화면 상태를 확인해 주세요.", failures);
            throw;
        }
    }

    public static string MoveTo(int slot, string device, CancellationToken cancellationToken)
    {
        if (IntPtr.Size != 8) return "화면 배치 변경에는 64비트 앱이 필요합니다.";
        using var operation = ApplyService.UserOperationGate.Enter(cancellationToken);
        using (ApplyService.DisplayOperationGate.Enter(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MoveCore(slot, device, cancellationToken);
        }
    }

    private static string MoveCore(int slot, string device, CancellationToken cancellationToken)
    {
        var live = LiveConnection.Open();
        var config = LoadConfig();
        uint original = live.VerifyConfig(config); var order = Decode(original);
        int source = config.Segments.FindIndex(s => SplitWindowMover.NormalizeDevice(s.VirtualDevice) == SplitWindowMover.NormalizeDevice(device));
        if (source < 0) return "현재 포커스된 창이 있는 화면은 DesktopSplitter 분할 화면이 아닙니다.";
        var next = Swap(order, source, slot);
        uint desired = Encode(next);
        if (desired == original) return $"현재 화면이 이미 배치 {slot}번에 있습니다.";
        var monitors = DisplayConfig.Enumerate();
        var plan = Plan(monitors, config, next);
        var transaction = DisplayConfig.PreparePositions(DisplayArranger.SnapshotLayout(monitors), plan);
        if (live.Query() != original) throw new InvalidOperationException("화면 배치가 바뀌었습니다. 단축키를 다시 눌러 주세요.");
        cancellationToken.ThrowIfCancellationRequested();
        Execute(transaction.Apply, () => live.Exchange(original, desired), transaction.Rollback, () => {
            uint current = live.Query();
            if (current == desired) live.Exchange(desired, original);
            else if (current != original) throw new InvalidOperationException("합성기 배치가 변경되어 복원을 중단했습니다.");
        });
        return $"현재 화면 전체를 배치 {slot}번과 맞바꿨습니다. 해상도와 주 모니터는 유지합니다.";
    }

    public static IReadOnlyList<SplitTarget> ResolveTargets(IReadOnlyList<SplitTarget> targets, bool requireLiveRouting = false)
    {
        var live = LiveConnection.TryOpen();
        if (live is null)
        {
            if (requireLiveRouting && targets.Count > 0)
                throw new InvalidOperationException("현재 칸과 화면의 연결을 확인하지 못했습니다. 이전 번호를 추측해 사용하지 않습니다.");
            return targets; // Legacy window helpers only; matrix capture/selection require verified routing.
        }
        var config = LoadConfig();
        return MapTargets(targets, config, Decode(live.VerifyConfig(config)));
    }

    public static IReadOnlyList<SplitTarget> MapTargets(IReadOnlyList<SplitTarget> targets, CompositorConfig config, IReadOnlyList<int> order)
    {
        _ = Encode(order);
        if (config.Segments.Count != order.Count) throw new InvalidOperationException("분할 칸 수가 바뀌었습니다.");
        return order.Select((source, slot) => {
            string device = config.Segments[source].VirtualDevice;
            var target = targets.SingleOrDefault(t => SplitWindowMover.NormalizeDevice(t.DeviceName) == SplitWindowMover.NormalizeDevice(device))
                ?? throw new InvalidOperationException("분할 화면 연결 상태가 바뀌었습니다.");
            return target with { Number = slot + 1 };
        }).ToArray();
    }

    private static CompositorConfig LoadConfig() => JsonSerializer.Deserialize<CompositorConfig>(File.ReadAllText(ApplyService.ConfigPath))
        ?? throw new InvalidOperationException("실행 중인 분할 설정을 읽지 못했습니다.");

    private sealed class LiveConnection(nint window, uint message)
    {
        public static LiveConnection Open() => TryOpen() ?? throw new InvalidOperationException("화면 배치 변경에는 새 합성기가 필요합니다. 새 빌드에서 분할을 다시 적용해 주세요.");
        public static LiveConnection? TryOpen()
        {
            uint message = RegisterWindowMessageW("DesktopSplitter.LiveLayout.v1");
            nint window = FindWindowExW((nint)(-3), 0, "DeskSplitCompositorWindow", null);
            if (window == 0) window = FindWindowExW(0, 0, "DeskSplitCompositorWindow", null);
            if (window == 0 || message == 0) return null;
            GetWindowThreadProcessId(window, out uint pid);
            using var process = Process.GetProcessById((int)pid);
            if (!process.ProcessName.Equals("dscomp", StringComparison.OrdinalIgnoreCase)) return null;
            var live = new LiveConnection(window, message);
            uint routing = live.Query();
            if ((routing & 0xFFFF0000) != Signature) return null;
            _ = Decode(routing);
            return live;
        }
        private uint Send(nuint command, nint payload)
        {
            if (SendMessageTimeoutW(window, message, command, payload, 0x23, 1500, out nuint result) == 0)
                throw new InvalidOperationException("화면 합성기가 응답하지 않습니다. 화면 배치 변경을 중단했습니다.");
            return unchecked((uint)result);
        }
        public uint Query() => Send(0, 0);
        public uint VerifyConfig(CompositorConfig config)
        {
            if (Send(2, 0) != Fingerprint(config))
                throw new InvalidOperationException("실행 중인 합성기와 저장된 분할 설정이 다릅니다. 앱에서 분할을 다시 적용해 주세요.");
            uint routing = Query();
            if (Decode(routing).Length != config.Segments.Count)
                throw new InvalidOperationException("실행 중인 합성기와 저장된 분할 설정이 다릅니다. 앱에서 분할을 다시 적용해 주세요.");
            return routing;
        }
        public void Exchange(uint expected, uint desired)
        {
            if (IntPtr.Size != 8) throw new InvalidOperationException("화면 배치 변경에는 64비트 앱이 필요합니다.");
            if (Send(1, unchecked((nint)(((ulong)expected << 32) | desired))) != desired)
                throw new InvalidOperationException("합성기가 화면 배치 변경을 거부했습니다.");
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint RegisterWindowMessageW(string text);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint FindWindowExW(nint parent, nint after, string className, string? name);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern nint SendMessageTimeoutW(nint window, uint message, nuint command, nint payload, uint flags, uint timeout, out nuint result);
    }
}
